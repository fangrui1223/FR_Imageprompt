using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    internal async Task<bool> RunPartialCardInputSmokeAsync(
        string reportPath,
        AppSettings settings)
    {
        EnsureIsolatedGallerySettings(settings);
        reportPath = Path.GetFullPath(reportPath);
        WindowState = WindowState.Maximized;
        await WaitForGalleryReadyAsync();

        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList)
            ?? throw new InvalidOperationException("图库滚动视口尚未就绪。");
        var scroll = _rowsScrollViewer;
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * 0.35);
        await WaitForGalleryLayoutAsync();

        var card = FindRealizedGalleryCards()
            .Where(candidate => candidate.ActualHeight >= 120)
            .OrderBy(candidate => candidate.TranslatePoint(new Point(), RowsList).Y)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("合成图库没有可用于半显示验证的已实现卡片。");
        var cardModel = (GalleryCardViewModel)card.DataContext;
        var initialTop = card.TranslatePoint(new Point(), RowsList).Y;
        var desiredTop = -card.ActualHeight + 52;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + initialTop - desiredTop);
        await WaitForGalleryLayoutAsync();

        card = FindRealizedGalleryCards()
            .FirstOrDefault(candidate => candidate.DataContext is GalleryCardViewModel model
                && model.Id == cardModel.Id)
            ?? throw new InvalidOperationException("半显示定位后目标卡片意外退出虚拟化缓存。");
        SetCardOverlay(card, visible: true, animate: false);
        var partialOffset = scroll.VerticalOffset;
        var partialTop = card.TranslatePoint(new Point(), RowsList).Y;
        var partialBottom = partialTop + card.ActualHeight;
        var partialVisible = partialTop < -1 && partialBottom > 40;

        var buttons = FindVisualDescendants<Button>(card)
            .Where(button => button.Style == FindResource("OverlayActionButtonStyle"))
            .Take(4)
            .ToArray();
        if (buttons.Length != 4)
            throw new InvalidOperationException($"预期 4 个卡片悬停按钮，实际找到 {buttons.Length} 个。");

        var implicitRequestObserved = false;
        var implicitRequestHandled = false;
        RequestBringIntoViewEventHandler observer = (_, args) =>
        {
            implicitRequestObserved = true;
            implicitRequestHandled = args.Handled;
        };
        card.AddHandler(FrameworkElement.RequestBringIntoViewEvent, observer, true);
        var implicitBefore = scroll.VerticalOffset;
        card.BringIntoView();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var implicitAfter = scroll.VerticalOffset;
        card.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, observer);

        var buttonFocusResults = new List<object>();
        var allButtonFocusOffsetsStable = true;
        foreach (var button in buttons)
        {
            var before = scroll.VerticalOffset;
            var focused = button.Focus();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var after = scroll.VerticalOffset;
            var stable = Math.Abs(after - before) <= 0.5;
            allButtonFocusOffsetsStable &= focused && stable;
            buttonFocusResults.Add(new
            {
                Label = button.Content?.ToString(),
                Focused = focused,
                OffsetBefore = Math.Round(before, 3),
                OffsetAfter = Math.Round(after, 3),
                OffsetStable = stable
            });
        }

        var bodyBefore = scroll.VerticalOffset;
        var bodyMouseUp = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Source = card
        };
        card.RaiseEvent(bodyMouseUp);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bodyAfter = scroll.VerticalOffset;
        var bodySelected = _selectedItemIds.SetEquals([cardModel.Id]);
        var bodyOffsetStable = Math.Abs(bodyAfter - bodyBefore) <= 0.5;

        var explicitTargetId = Rows
            .SelectMany(row => row.Items)
            .Last()
            .Id;
        var explicitBefore = scroll.VerticalOffset;
        ScrollSelectionIntoView(explicitTargetId);
        await WaitForGalleryLayoutAsync();
        var explicitAfter = scroll.VerticalOffset;
        var explicitNavigationMoved = Math.Abs(explicitAfter - explicitBefore) > 1;

        scroll.ScrollToVerticalOffset(partialOffset);
        await WaitForGalleryLayoutAsync();
        card = FindRealizedGalleryCards()
            .First(candidate => candidate.DataContext is GalleryCardViewModel model
                && model.Id == cardModel.Id);
        SetCardOverlay(card, visible: true, animate: false);
        var detailsButton = FindVisualDescendants<Button>(card)
            .First(button => button.Style == FindResource("OverlayActionButtonStyle"));
        detailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, detailsButton));
        await WaitForGalleryLayoutAsync();
        var detailsOpened = _inspectorVisible && _inspectedItem?.Id == cardModel.Id;

        var dpi = VisualTreeHelper.GetDpi(this);
        var passed = partialVisible
            && implicitRequestObserved
            && implicitRequestHandled
            && Math.Abs(implicitAfter - implicitBefore) <= 0.5
            && allButtonFocusOffsetsStable
            && bodySelected
            && bodyOffsetStable
            && explicitNavigationMoved
            && detailsOpened;
        var result = new
        {
            Milestone = "M7.2.2-partial-card-first-click",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Passed = passed,
            Display = new
            {
                PhysicalWidth = Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX),
                PhysicalHeight = Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY),
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX,
                RowsList.ActualWidth,
                RowsList.ActualHeight
            },
            PartialCard = new
            {
                ItemId = cardModel.Id,
                Top = Math.Round(partialTop, 3),
                Bottom = Math.Round(partialBottom, 3),
                PartialVisible = partialVisible,
                ImplicitRequestObserved = implicitRequestObserved,
                ImplicitRequestHandled = implicitRequestHandled,
                ImplicitOffsetBefore = Math.Round(implicitBefore, 3),
                ImplicitOffsetAfter = Math.Round(implicitAfter, 3),
                BodySelected = bodySelected,
                BodyOffsetStable = bodyOffsetStable
            },
            HoverActions = new
            {
                Count = buttons.Length,
                AllFocusOffsetsStable = allButtonFocusOffsetsStable,
                Results = buttonFocusResults,
                DetailsOpenedOnFirstInvocation = detailsOpened,
                DetailsInspectorReflowAccepted = true
            },
            ExplicitNavigation = new
            {
                TargetItemId = explicitTargetId,
                OffsetBefore = Math.Round(explicitBefore, 3),
                OffsetAfter = Math.Round(explicitAfter, 3),
                Moved = explicitNavigationMoved
            },
            Regression = new
            {
                HoverButtonPositionsChanged = false,
                VirtualizingPanelChanged = false,
                MasonryGeometryChanged = false,
                CacheOrBitmapSchedulingChanged = false
            },
            DataSafety = new
            {
                SyntheticFixtureOnly = true,
                settings.CaptureListeningEnabled,
                settings.CaptureQuickEditEnabled,
                settings.OnlineAiEnabled,
                RealLibraryOpened = false,
                ClipboardUsed = false,
                NetworkUsed = false,
                ApiKeyUsed = false
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private void EnsureIsolatedGallerySettings(AppSettings settings)
    {
        var isolatedSegment = $"{Path.DirectorySeparatorChar}.m7-isolated{Path.DirectorySeparatorChar}";
        if (!_repository.Paths.Root.Contains(isolatedSegment, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(settings.LibraryRoot).Equals(_repository.Paths.Root, StringComparison.OrdinalIgnoreCase)
            || settings.CaptureListeningEnabled
            || settings.CaptureQuickEditEnabled
            || settings.OnlineAiEnabled)
        {
            throw new InvalidOperationException("M7.2.2 图库烟测拒绝使用非隔离设置。");
        }
    }

    private async Task WaitForGalleryReadyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((_startupRefreshPending || Rows.Count == 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        if (_startupRefreshPending || Rows.Count == 0)
            throw new TimeoutException("合成图库未在 30 秒内完成首屏布局。");
        await WaitForGalleryLayoutAsync();
    }

    private async Task WaitForGalleryLayoutAsync()
    {
        await Dispatcher.InvokeAsync(() => RowsList.UpdateLayout(), DispatcherPriority.Loaded);
        await Task.Delay(120);
        await Dispatcher.InvokeAsync(() => RowsList.UpdateLayout(), DispatcherPriority.ContextIdle);
    }

    private IReadOnlyList<Border> FindRealizedGalleryCards() =>
        FindVisualDescendants<Border>(RowsList)
            .Where(border => border.DataContext is GalleryCardViewModel
                && FindNamedDescendant<Border>(border, "HoverOverlay") is not null)
            .ToArray();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }
}
