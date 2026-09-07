using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunInputSmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8-02 输入烟测");
        reportPath = Path.GetFullPath(reportPath);
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) => { Loaded -= handler; loaded.TrySetResult(); };
            Loaded += handler;
            await loaded.Task;
        }
        WindowState = WindowState.Maximized;
        await WaitForLayoutAsync();

        var grouped = _items.First(item => item.GroupId is not null);
        var groupIds = _items.Where(item => item.GroupId == grouped.GroupId).Select(item => item.Id).Order().ToArray();
        var selected = BoardInteractionEngine.SelectItem(_items, new HashSet<long>(), grouped.Id, additive: false);
        _selectedIds.Clear();
        _selectedIds.UnionWith(selected);
        RenderVisibleItems();
        var groupSelectedAtomically = groupIds.SequenceEqual(_selectedIds.Order());

        var rotated = _items.First(item => Math.Abs(item.Rotation % 90) > 0.01);
        var bounds = BoardCameraEngine.ItemBounds(rotated);
        var baseline = _selectedIds.ToHashSet();
        var marquee = new BoardWorldRect(bounds.X + bounds.Width, bounds.Y + bounds.Height, -bounds.Width, -bounds.Height);
        var marqueeSelection = BoardInteractionEngine.SelectItemsInMarquee(_items, marquee, baseline, additive: true);
        _selectedIds.Clear();
        _selectedIds.UnionWith(marqueeSelection);
        RenderVisibleItems();
        MarqueeSelection.Width = 180;
        MarqueeSelection.Height = 120;
        MarqueeSelection.Margin = new Thickness(80, 80, 0, 0);
        MarqueeSelection.Visibility = Visibility.Visible;
        await WaitForLayoutAsync();
        var marqueeVisible = MarqueeSelection.IsVisible && MarqueeSelection.ActualWidth > 0;
        MarqueeSelection.Visibility = Visibility.Collapsed;

        var beforePan = _viewport;
        StartPan(new Point(200, 200));
        _viewport = _panStartViewport with
        {
            OffsetX = _panStartViewport.OffsetX + 35,
            OffsetY = _panStartViewport.OffsetY - 22
        };
        ApplyViewportMatrix();
        RenderVisibleItems();
        EndPan();
        var sharedPanPathMoved = NearlyEqual(_viewport.OffsetX, beforePan.OffsetX + 35)
            && NearlyEqual(_viewport.OffsetY, beforePan.OffsetY - 22);

        var note = _realizedNotes.Values.FirstOrDefault();
        var textEditingProtected = false;
        if (note is not null)
        {
            note.Editor.Focus();
            await Dispatcher.InvokeAsync(() => { });
            textEditingProtected = IsTextEditingFocus()
                && BoardInteractionEngine.ResolvePriority(false, true, false, false, _focusController.IsActive)
                    == BoardInputPriority.TextEditing;
            Keyboard.Focus(BoardViewport);
        }

        var jitterRejected = !BoardInteractionEngine.ExceedsDragThreshold(0, 0, 3, 3)
            && BoardInteractionEngine.ExceedsDragThreshold(0, 0, 5, 0);
        var plainRightPanPolicy = BoardInteractionEngine.ShouldPanCanvasWithRightDrag(
                controlPressed: false,
                windowMaximized: false)
            && BoardInteractionEngine.ShouldPanCanvasWithRightDrag(
                controlPressed: false,
                windowMaximized: true)
            && !BoardInteractionEngine.ShouldPanCanvasWithRightDrag(
                controlPressed: true,
                windowMaximized: false)
            && !BoardInteractionEngine.ShouldPanCanvasWithRightDrag(
                controlPressed: true,
                windowMaximized: true);
        var dpi = VisualTreeHelper.GetDpi(this);
        var process = Process.GetCurrentProcess();
        var passed = groupSelectedAtomically
            && _selectedIds.Contains(rotated.Id)
            && marqueeVisible
            && sharedPanPathMoved
            && textEditingProtected
            && jitterRejected
            && plainRightPanPolicy;
        var report = new
        {
            Milestone = "M8-02-input-selection-ui-smoke",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Selection = new
            {
                GroupItemCount = groupIds.Length,
                GroupSelectedAtomically = groupSelectedAtomically,
                RotatedMarqueeHit = _selectedIds.Contains(rotated.Id),
                MarqueeVisible = marqueeVisible
            },
            Input = new
            {
                SharedPanPathMoved = sharedPanPathMoved,
                TextEditingProtected = textEditingProtected,
                DragJitterRejected = jitterRejected,
                PlainRightPanPolicy = plainRightPanPolicy,
                DragThresholdDip = BoardInteractionEngine.DefaultDragThreshold
            },
            Process = new
            {
                WorkingSetBytes = process.WorkingSet64,
                PrivateMemoryBytes = process.PrivateMemorySize64,
                HandleCount = process.HandleCount,
                ThreadCount = process.Threads.Count,
                Responding = process.Responding
            },
            DataSafety = IsolatedSafety(settings),
            Passed = passed
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(passed ? "M8-02 隔离输入烟测通过" : "M8-02 隔离输入烟测失败");
    }

    private void EnsureIsolatedM8Settings(AppSettings settings, string area)
    {
        var m8Segment = $"{Path.DirectorySeparatorChar}.m8-isolated{Path.DirectorySeparatorChar}";
        var m10Segment = $"{Path.DirectorySeparatorChar}.m10-isolated{Path.DirectorySeparatorChar}";
        if ((!_repository.Paths.Root.Contains(m8Segment, StringComparison.OrdinalIgnoreCase)
                && !_repository.Paths.Root.Contains(m10Segment, StringComparison.OrdinalIgnoreCase))
            || !Path.GetFullPath(settings.LibraryRoot).Equals(_repository.Paths.Root, StringComparison.OrdinalIgnoreCase)
            || settings.CaptureListeningEnabled
            || settings.CaptureQuickEditEnabled
            || settings.OnlineAiEnabled)
            throw new InvalidOperationException($"{area}拒绝使用非隔离设置。");
    }

    private static object IsolatedSafety(AppSettings settings) => new
    {
        SyntheticFixtureOnly = true,
        settings.CaptureListeningEnabled,
        settings.CaptureQuickEditEnabled,
        settings.OnlineAiEnabled,
        RealLibraryOpened = false,
        ClipboardUsed = false,
        NetworkUsed = false,
        ApiKeyUsed = false
    };
}
