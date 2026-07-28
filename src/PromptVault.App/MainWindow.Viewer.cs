using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private int _viewerIndex = -1;
    private bool _viewerDragging;
    private bool _inspectorHiddenForViewer;
    private Point _viewerLastPoint;
    private CancellationTokenSource? _viewerLoadCancellation;
    private readonly ImmersiveImageCache _immersiveImageCache = ImmersiveImageCache.Shared;

    private void ShowImmersiveViewer(long itemId)
    {
        var index = _items.ToList().FindIndex(x => x.Id == itemId);
        if (index < 0) return;
        _viewerIndex = index;
        if (_inspectorVisible)
        {
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorSplitter.Visibility = Visibility.Collapsed;
            _inspectorHiddenForViewer = true;
        }
        if (_transparentMode) EnterTransparentViewerBackdrop();
        ImmersiveViewer.Visibility = Visibility.Visible;
        ResetImmersiveTransform();
        _ = LoadViewerImageAsync();
        Focus();
    }

    private async Task LoadViewerImageAsync()
    {
        if (_viewerIndex < 0 || _viewerIndex >= _items.Count) return;
        _viewerLoadCancellation?.Cancel();
        _viewerLoadCancellation = new CancellationTokenSource();
        var token = _viewerLoadCancellation.Token;
        var item = _items[_viewerIndex];
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var path = ResolveOriginalPath(item);
            var decodeWidth = (int)Math.Clamp(ActualWidth * 1.6, 1200, 2800);
            var result = await _immersiveImageCache.LoadAsync(path, decodeWidth, token);
            token.ThrowIfCancellationRequested();
            ImmersiveImage.Source = result.Image;
            stopwatch.Stop();
            var snapshot = _immersiveImageCache.GetSnapshot();
            DevelopmentPerformanceTrace.Event("immersive-image-load", new
            {
                cacheHit = result.CacheHit,
                elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                decodeWidth,
                cacheEntries = snapshot.EntryCount,
                cachedMiB = Math.Round(snapshot.CachedBytes / 1024d / 1024d, 3),
                budgetMiB = Math.Round(snapshot.MemoryBudgetBytes / 1024d / 1024d, 3)
            });
            PrefetchViewerNeighbor(_viewerIndex - 1, decodeWidth, path);
            PrefetchViewerNeighbor(_viewerIndex + 1, decodeWidth, path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ToastService.Show(this, $"无法打开图片：{ex.Message}"); }
    }

    private void PrefetchViewerNeighbor(int index, int decodeWidth, string currentPath)
    {
        if (_items.Count < 2) return;
        var normalizedIndex = (index + _items.Count) % _items.Count;
        var path = ResolveOriginalPath(_items[normalizedIndex]);
        if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) return;
        _ = _immersiveImageCache.PrefetchAsync(path, decodeWidth);
    }

    private void NavigateViewer(int offset)
    {
        if (_items.Count == 0) return;
        _viewerIndex = (_viewerIndex + offset + _items.Count) % _items.Count;
        ResetImmersiveTransform();
        _ = LoadViewerImageAsync();
    }

    private void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        DevelopmentPerformanceTrace.Event("preview-key", new
        {
            key = e.Key.ToString(),
            imeProcessedKey = e.ImeProcessedKey.ToString(),
            systemKey = e.SystemKey.ToString(),
            modifiers = Keyboard.Modifiers.ToString(),
            galleryShortcutContext = _galleryShortcutContext,
            focusedType = Keyboard.FocusedElement?.GetType().Name
        });
        if (DevelopmentPerformanceTrace.IsEnabled
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            && e.Key == Key.F9)
        {
            StartDevelopmentScrollProbe();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F)
        {
            FocusInstantSearch();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.K)
        {
            ToggleCommandPalette();
            e.Handled = true;
            return;
        }

        if (CommandPaletteOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseCommandPalette();
                FocusGalleryInput();
                Focus();
                e.Handled = true;
            }
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.M)
        {
            ToggleTransparentMode();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && (e.Key == Key.M || e.SystemKey == Key.M))
        {
            _ = CollectCurrentExternalSelectionAsync();
            e.Handled = true;
            return;
        }

        if (ImmersiveViewer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) CloseImmersiveViewer();
            else if (e.Key == Key.Left) NavigateViewer(-1);
            else if (e.Key == Key.Right) NavigateViewer(1);
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) ResetImmersiveTransform();
            else return;
            e.Handled = true;
            return;
        }

        if (HandleGallerySelectionKey(e)) e.Handled = true;
    }

    private void MainWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        NotePointerFocusTarget(e.OriginalSource as DependencyObject);
        if (TryStartCtrlRightDrag(e)) return;
        if (ImmersiveViewer.Visibility != Visibility.Visible) return;
        if (e.ChangedButton == MouseButton.XButton1) NavigateViewer(-1);
        else if (e.ChangedButton == MouseButton.XButton2) NavigateViewer(1);
        else return;
        e.Handled = true;
    }

    private void ViewerLeftEdgeClick(object sender, MouseButtonEventArgs e) { NavigateViewer(-1); e.Handled = true; }
    private void ViewerRightEdgeClick(object sender, MouseButtonEventArgs e) { NavigateViewer(1); e.Handled = true; }

    private void ViewerEdgeMouseEnter(object sender, MouseEventArgs e)
    {
        var glow = ReferenceEquals(sender, ViewerLeftEdge) ? ViewerLeftGlow : ViewerRightGlow;
        glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, VisualModeService.Motion(MotionToken.Fast)));
    }

    private void ViewerEdgeMouseLeave(object sender, MouseEventArgs e)
    {
        var glow = ReferenceEquals(sender, ViewerLeftEdge) ? ViewerLeftGlow : ViewerRightGlow;
        glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, VisualModeService.Motion(MotionToken.Standard)));
    }

    private void ViewerCloseMouseEnter(object sender, MouseEventArgs e) =>
        ViewerCloseButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0.72, VisualModeService.Motion(MotionToken.Fast)));

    private void ViewerCloseMouseLeave(object sender, MouseEventArgs e) =>
        ViewerCloseButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, VisualModeService.Motion(MotionToken.Standard)));
    private void PreviousViewerClick(object sender, RoutedEventArgs e) => NavigateViewer(-1);
    private void NextViewerClick(object sender, RoutedEventArgs e) => NavigateViewer(1);
    private void CloseViewerClick(object sender, RoutedEventArgs e) => CloseImmersiveViewer();

    private void CloseImmersiveViewer()
    {
        var wasTransparentViewer = _transparentMode && ImmersiveViewer.Visibility == Visibility.Visible;
        _viewerLoadCancellation?.Cancel();
        _viewerIndex = -1;
        ImmersiveImage.Source = null;
        ImmersiveViewer.Visibility = Visibility.Collapsed;
        ResetImmersiveTransform();
        if (_inspectorHiddenForViewer)
        {
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorSplitter.Visibility = Visibility.Visible;
            _inspectorHiddenForViewer = false;
        }
        if (wasTransparentViewer) LeaveTransparentViewerBackdrop();
    }

    private void EnterTransparentViewerBackdrop()
    {
        HideTopPanel();
        HideLeftPanel();
        TopPanel.Visibility = Visibility.Collapsed;
        LeftPanel.Visibility = Visibility.Collapsed;
        FadeGalleryLayer(0, MotionToken.Fast, false);
    }

    private void LeaveTransparentViewerBackdrop()
    {
        TopPanel.Visibility = Visibility.Visible;
        LeftPanel.Visibility = Visibility.Visible;
        FadeGalleryLayer(1, MotionToken.Standard, true);
    }

    private void FadeGalleryLayer(double targetOpacity, MotionToken motion, bool hitTestWhenDone)
    {
        if (targetOpacity <= 0) GalleryLayer.IsHitTestVisible = false;
        var duration = VisualModeService.Motion(motion);
        if (duration == TimeSpan.Zero)
        {
            GalleryLayer.BeginAnimation(OpacityProperty, null);
            GalleryLayer.Opacity = targetOpacity;
            GalleryLayer.IsHitTestVisible = hitTestWhenDone;
            return;
        }
        var animation = new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        if (targetOpacity > 0)
        {
            animation.Completed += (_, _) => GalleryLayer.IsHitTestVisible = hitTestWhenDone;
        }
        GalleryLayer.BeginAnimation(OpacityProperty, animation);
    }

    private void ImmersiveMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.14 : 1 / 1.14;
        var scale = Math.Clamp(ImmersiveScale.ScaleX * factor, 1, 10);
        ImmersiveScale.ScaleX = ImmersiveScale.ScaleY = scale;
        if (scale <= 1) ImmersiveTranslate.X = ImmersiveTranslate.Y = 0;
        e.Handled = true;
    }

    private void ImmersiveMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ImmersiveScale.ScaleX <= 1) return;
        _viewerDragging = true;
        _viewerLastPoint = e.GetPosition(ImmersiveViewer);
        ImmersiveImage.CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void ImmersiveMouseMove(object sender, MouseEventArgs e)
    {
        if (!_viewerDragging) return;
        var point = e.GetPosition(ImmersiveViewer);
        ImmersiveTranslate.X += point.X - _viewerLastPoint.X;
        ImmersiveTranslate.Y += point.Y - _viewerLastPoint.Y;
        _viewerLastPoint = point;
    }

    private void ImmersiveMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewerDragging) return;
        _viewerDragging = false;
        ImmersiveImage.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void ResetImmersiveTransform()
    {
        ImmersiveScale.ScaleX = ImmersiveScale.ScaleY = 1;
        ImmersiveTranslate.X = ImmersiveTranslate.Y = 0;
    }
}
