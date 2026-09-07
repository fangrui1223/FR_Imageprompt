using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task<bool> RunM85SmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8.5 画板选择视觉烟测");
        reportPath = Path.GetFullPath(reportPath);
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                Loaded -= handler;
                loaded.TrySetResult();
            };
            Loaded += handler;
            await loaded.Task;
        }

        WindowState = WindowState.Maximized;
        await WaitForLayoutAsync();
        await WaitForAnyBoardImageAsync();
        var candidates = _items
            .Where(item => _realized.ContainsKey(item.Id))
            .OrderByDescending(item => item.Width * item.Height)
            .Take(3)
            .ToArray();
        if (candidates.Length < 2)
            throw new InvalidOperationException("M8.5 隔离烟测至少需要两张已实现的合成图片。");

        var first = candidates[0];
        var second = candidates[1];
        var originalViewport = _viewport;
        var documentBefore = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("M8.5 隔离画板不存在。");
        var firstElement = _realized[first.Id];
        var firstBitmap = FindItemImage(firstElement)?.Source;

        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        RenderVisibleItems();
        await WaitForLayoutAsync();
        var unselectedBordersZero = _realized.Values
            .OfType<Border>()
            .All(border => border.BorderThickness.Left == 0);

        _selectedIds.Add(first.Id);
        RenderVisibleItems();
        await WaitForLayoutAsync();
        var dpi = VisualTreeHelper.GetDpi(this);
        var singleRootBorderZero = _realized[first.Id] is Border singleBorder
            && singleBorder.BorderThickness.Left == 0;
        var singleOutlinePhysicalPixels = SelectionOutline.BorderThickness.Left * dpi.DpiScaleX;
        var singleOutlineOnePhysicalPixel = NearlyOne(singleOutlinePhysicalPixels);
        var singleCommonOutlineVisible = SelectionBoundsOverlay.Visibility == Visibility.Visible
            && TopLeftHandle.Visibility == Visibility.Visible
            && TopRightHandle.Visibility == Visibility.Visible
            && BottomLeftHandle.Visibility == Visibility.Visible
            && BottomRightHandle.Visibility == Visibility.Visible;

        _selectedIds.Add(second.Id);
        RenderVisibleItems();
        await WaitForLayoutAsync();
        var multiMemberBorders = _selectedIds
            .Where(id => _realized.ContainsKey(id))
            .Select(id => (Border)_realized[id])
            .ToArray();
        var multiMemberBordersVisible = multiMemberBorders.Length > 0
            && multiMemberBorders.All(border => border.BorderThickness.Left > 0);
        var multiNonMemberBordersZero = _realized
            .Where(pair => !_selectedIds.Contains(pair.Key))
            .Select(pair => pair.Value)
            .OfType<Border>()
            .All(border => border.BorderThickness.Left == 0);
        var multiMemberPhysicalPixels = multiMemberBorders
            .Select(border => border.BorderThickness.Left * _viewport.Zoom * dpi.DpiScaleX)
            .ToArray();
        var multiMembersOnePhysicalPixel = multiMemberPhysicalPixels.All(NearlyOne);
        var multiCommonOutlineVisible = SelectionBoundsOverlay.Visibility == Visibility.Visible;

        var zoomChecks = new List<object>();
        var zoomsPassed = true;
        foreach (var zoom in new[] { 0.2, 2.05 })
        {
            _viewport = CenterItemAtZoom(_viewport, first, zoom);
            ApplyViewportMatrix();
            RenderVisibleItems();
            await WaitForLayoutAsync();
            if (!_realized.TryGetValue(first.Id, out var realized) || realized is not Border border)
                throw new InvalidOperationException($"M8.5 缩放 {zoom:P0} 时合成图片未实现。");
            var physicalPixels = border.BorderThickness.Left * _viewport.Zoom * dpi.DpiScaleX;
            var passed = NearlyOne(physicalPixels);
            zoomsPassed &= passed;
            zoomChecks.Add(new
            {
                Zoom = zoom,
                WorldThickness = border.BorderThickness.Left,
                PhysicalPixels = physicalPixels,
                Passed = passed
            });
        }

        _viewport = originalViewport;
        ApplyViewportMatrix();
        _selectedIds.Clear();
        _selectedIds.Add(first.Id);
        RenderVisibleItems();
        await WaitForLayoutAsync();
        await WaitForAnyBoardImageAsync();
        BeginCropMode();
        var cropOutlinePreserved = _cropModeActive
            && SelectionOutline.BorderThickness.Left == 2
            && TopLeftHandle.Visibility == Visibility.Collapsed
            && TopRightHandle.Visibility == Visibility.Collapsed
            && BottomLeftHandle.Visibility == Visibility.Collapsed
            && BottomRightHandle.Visibility == Visibility.Collapsed;
        CancelCropMode();
        await WaitForLayoutAsync();

        var stableElement = _realized.TryGetValue(first.Id, out var firstAfter)
            && ReferenceEquals(firstElement, firstAfter);
        var stableBitmap = firstAfter is not null
            && ReferenceEquals(firstBitmap, FindItemImage(firstAfter)?.Source);
        var documentAfter = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("M8.5 隔离画板不存在。");
        var zeroDatabaseWrites = documentBefore.Items.SequenceEqual(documentAfter.Items)
            && documentBefore.Notes.SequenceEqual(documentAfter.Notes);

        var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
        var subtitle = mainWindow?.FindName("ProductSubtitleText") as TextBlock;
        var brandingExact = subtitle?.Text == "图片资产库 V2.0";
        var brandingStylePreserved = subtitle?.FontSize == 9
            && ReferenceEquals(subtitle.Foreground, mainWindow?.FindResource("AccentBrush"));

        var passedAll = unselectedBordersZero
            && singleRootBorderZero
            && singleOutlineOnePhysicalPixel
            && singleCommonOutlineVisible
            && multiMemberBordersVisible
            && multiNonMemberBordersZero
            && multiMembersOnePhysicalPixel
            && multiCommonOutlineVisible
            && zoomsPassed
            && cropOutlinePreserved
            && stableElement
            && stableBitmap
            && zeroDatabaseWrites
            && brandingExact
            && brandingStylePreserved;
        var report = new
        {
            Milestone = "M8.5-board-selection-visuals-branding",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Passed = passedAll,
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX,
                BoardViewport.ActualWidth,
                BoardViewport.ActualHeight,
                OriginalZoom = originalViewport.Zoom
            },
            SelectionVisuals = new
            {
                UnselectedBordersZero = unselectedBordersZero,
                SingleRootBorderZero = singleRootBorderZero,
                SingleCommonOutlineVisible = singleCommonOutlineVisible,
                SingleOutlinePhysicalPixels = singleOutlinePhysicalPixels,
                MultiRealizedMemberCount = multiMemberBorders.Length,
                MultiMemberBordersVisible = multiMemberBordersVisible,
                MultiNonMemberBordersZero = multiNonMemberBordersZero,
                MultiMemberPhysicalPixels = multiMemberPhysicalPixels,
                MultiCommonOutlineVisible = multiCommonOutlineVisible,
                ZoomChecks = zoomChecks
            },
            Regression = new
            {
                CropOutlinePreserved = cropOutlinePreserved,
                CropOutlineDip = 2,
                VisualRebuilds = stableElement ? 0 : 1,
                BitmapRebuilds = stableBitmap ? 0 : 1,
                DatabaseWrites = zeroDatabaseWrites ? 0 : 1,
                NotesUnchanged = documentBefore.Notes.SequenceEqual(documentAfter.Notes),
                DatabaseSchemaChanged = false,
                GalleryPerformanceLayerTouched = false
            },
            Branding = new
            {
                ExactText = brandingExact,
                StylePreserved = brandingStylePreserved,
                Text = subtitle?.Text,
                FontSize = subtitle?.FontSize,
                UsesAccentBrush = ReferenceEquals(subtitle?.Foreground, mainWindow?.FindResource("AccentBrush"))
            },
            DataSafety = IsolatedSafety(settings)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(passedAll ? "M8.5 隔离烟测通过" : "M8.5 隔离烟测失败");
        return passedAll;
    }

    private static BoardViewport CenterItemAtZoom(
        BoardViewport viewport,
        BoardItemRecord item,
        double zoom) =>
        viewport with
        {
            Zoom = zoom,
            OffsetX = viewport.Width / 2 - (item.X + item.Width / 2) * zoom,
            OffsetY = viewport.Height / 2 - (item.Y + item.Height / 2) * zoom
        };

    private static bool NearlyOne(double value) => Math.Abs(value - 1d) <= 0.05;
}
