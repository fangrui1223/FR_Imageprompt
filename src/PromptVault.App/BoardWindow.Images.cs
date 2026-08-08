using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private readonly ImmersiveImageCache _boardImmersiveImageCache = ImmersiveImageCache.Shared;
    private CancellationTokenSource? _boardImageLoadCancellation;
    private long _boardImageGeneration;
    private long _boardPreviewVisibleCount;
    private long _boardHighResolutionRequestCount;
    private long _boardHighResolutionCompleteCount;
    private long _boardHighResolutionCancelCount;
    private long _boardHighResolutionFailureCount;
    private long _boardHighResolutionCacheHitCount;

    private void BeginProgressiveFocusLoad(BoardCameraTransition transition)
    {
        CancelProgressiveFocusLoad();
        SetRealizedBitmapScalingMode(BitmapScalingMode.LowQuality);
        if (transition.IsRestore || transition.Target.ItemIds.Count == 0) return;
        var generation = ++_boardImageGeneration;
        var cancellation = new CancellationTokenSource();
        _boardImageLoadCancellation = cancellation;
        _boardPreviewVisibleCount++;
        _ = UpgradeFocusedImagesAsync(transition, generation, cancellation.Token);
    }

    private async Task UpgradeFocusedImagesAsync(
        BoardCameraTransition transition,
        long generation,
        CancellationToken cancellationToken)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var selected = transition.Target.ItemIds
            .Select(id => _items.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null)
            .Cast<BoardItemRecord>()
            .Where(item => _realized.ContainsKey(item.Id))
            .Where(item => transition.Target.Kind == BoardFocusTargetKind.SingleItem
                || BoardProgressiveImagePolicy.IsSignificant(
                    item.Width * transition.End.Zoom,
                    item.Height * transition.End.Zoom,
                    dpi))
            .OrderByDescending(item => item.Width * item.Height)
            .Take(BoardProgressiveImagePolicy.MaximumMultiSelectionUpgrades)
            .ToArray();
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpgradeItemAsync(item, transition.End.Zoom, dpi, generation, cancellationToken);
        }
        if (transition.Target.SingleItemId is { } currentId)
            PrefetchFocusedNeighbors(currentId, transition.End.Zoom, dpi);
    }

    private async Task UpgradeItemAsync(
        BoardItemRecord item,
        double targetZoom,
        double dpi,
        long generation,
        CancellationToken cancellationToken)
    {
        var original = ResolveItemOriginalPath(item);
        if (!File.Exists(original)) return;
        var decodeWidth = BoardProgressiveImagePolicy.SelectDecodeWidth(
            item.Width * targetZoom,
            item.Height * targetZoom,
            dpi);
        var stopwatch = Stopwatch.StartNew();
        _boardHighResolutionRequestCount++;
        try
        {
            var result = await _boardImmersiveImageCache.LoadAsync(original, decodeWidth, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _boardImageGeneration
                || !_realized.TryGetValue(item.Id, out var element)
                || FindItemImage(element) is not { } image) return;
            SetItemImageSource(element, result.Image, item);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _boardHighResolutionCompleteCount++;
            if (result.CacheHit) _boardHighResolutionCacheHitCount++;
            DevelopmentPerformanceTrace.Event("board-focus-image-load", new
            {
                itemId = item.Id,
                generation,
                decodeWidth,
                cacheHit = result.CacheHit,
                elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)
            });
        }
        catch (OperationCanceledException)
        {
            _boardHighResolutionCancelCount++;
        }
        catch (Exception ex)
        {
            _boardHighResolutionFailureCount++;
            AppLog.Warning("board-focus-image", "Focused image upgrade failed; current preview was preserved.", ex);
        }
    }

    private void PrefetchFocusedNeighbors(long currentId, double targetZoom, double dpi)
    {
        var ordered = _items.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray();
        var index = Array.FindIndex(ordered, item => item.Id == currentId);
        if (index < 0 || ordered.Length < 2) return;
        foreach (var offset in new[] { -1, 1 })
        {
            var neighbor = ordered[(index + offset + ordered.Length) % ordered.Length];
            var path = ResolveItemOriginalPath(neighbor);
            if (!File.Exists(path)) continue;
            var width = BoardProgressiveImagePolicy.SelectDecodeWidth(
                neighbor.Width * targetZoom,
                neighbor.Height * targetZoom,
                dpi);
            _ = _boardImmersiveImageCache.PrefetchAsync(path, width);
        }
    }

    private void CancelProgressiveFocusLoad()
    {
        _boardImageGeneration++;
        var cancellation = _boardImageLoadCancellation;
        _boardImageLoadCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void SetRealizedBitmapScalingMode(BitmapScalingMode mode)
    {
        foreach (var element in _realized.Values)
            if (FindItemImage(element) is { } image) RenderOptions.SetBitmapScalingMode(image, mode);
    }

    private static System.Windows.Controls.Image? FindItemImage(FrameworkElement element) =>
        element is Border { Child: Grid grid }
            ? grid.Children.OfType<System.Windows.Controls.Image>().FirstOrDefault()
            : null;

    private static Border? FindItemImageSurface(FrameworkElement element) =>
        element is Border { Child: Grid grid }
            ? grid.Children.OfType<Border>().FirstOrDefault(child => Equals(child.Tag, "BoardImageSurface"))
            : null;

    private void SetItemImageSource(FrameworkElement element, BitmapSource? bitmap, BoardItemRecord item)
    {
        if (FindItemImage(element) is { } sourceImage) sourceImage.Source = bitmap;
        if (FindItemImageSurface(element) is not { } surface) return;
        if (surface.Background is not ImageBrush brush)
        {
            brush = new ImageBrush
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            surface.Background = brush;
        }
        brush.ImageSource = bitmap;
        ApplyImageViewport(element, item);
    }

    private void ApplyImageViewport(FrameworkElement element, BoardItemRecord item)
    {
        if (FindItemImageSurface(element)?.Background is not ImageBrush brush) return;
        var viewport = GetDisplayCropViewport(item);
        brush.Viewbox = new Rect(viewport.X, viewport.Y, viewport.Width, viewport.Height);
    }
}
