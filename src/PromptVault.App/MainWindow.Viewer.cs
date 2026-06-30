using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private int _viewerIndex = -1;
    private bool _viewerDragging;
    private Point _viewerLastPoint;
    private CancellationTokenSource? _viewerLoadCancellation;

    private void ShowImmersiveViewer(long itemId)
    {
        var index = _items.ToList().FindIndex(x => x.Id == itemId);
        if (index < 0) return;
        _viewerIndex = index;
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
        try
        {
            var path = ResolveOriginalPath(item);
            var decodeWidth = (int)Math.Clamp(ActualWidth * 1.6, 1200, 2800);
            var bitmap = await Task.Run(() => ImagePipeline.LoadPreview(path, decodeWidth), token);
            token.ThrowIfCancellationRequested();
            ImmersiveImage.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ToastService.Show(this, $"无法打开图片：{ex.Message}"); }
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

        if (ImmersiveViewer.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Escape) CloseImmersiveViewer();
        else if (e.Key == Key.Left) NavigateViewer(-1);
        else if (e.Key == Key.Right) NavigateViewer(1);
        else if (e.Key == Key.D0 || e.Key == Key.NumPad0) ResetImmersiveTransform();
        else return;
        e.Handled = true;
    }

    private void MainWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
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
        glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(160)));
    }

    private void ViewerEdgeMouseLeave(object sender, MouseEventArgs e)
    {
        var glow = ReferenceEquals(sender, ViewerLeftEdge) ? ViewerLeftGlow : ViewerRightGlow;
        glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
    }

    private void ViewerCloseMouseEnter(object sender, MouseEventArgs e) =>
        ViewerCloseButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0.72, TimeSpan.FromMilliseconds(140)));

    private void ViewerCloseMouseLeave(object sender, MouseEventArgs e) =>
        ViewerCloseButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
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
        if (wasTransparentViewer) LeaveTransparentViewerBackdrop();
    }

    private void EnterTransparentViewerBackdrop()
    {
        HideTopPanel();
        HideLeftPanel();
        TopPanel.Visibility = Visibility.Collapsed;
        LeftPanel.Visibility = Visibility.Collapsed;
        FadeGalleryLayer(0, 160, false);
    }

    private void LeaveTransparentViewerBackdrop()
    {
        TopPanel.Visibility = Visibility.Visible;
        LeftPanel.Visibility = Visibility.Visible;
        FadeGalleryLayer(1, 220, true);
    }

    private void FadeGalleryLayer(double targetOpacity, int milliseconds, bool hitTestWhenDone)
    {
        if (targetOpacity <= 0) GalleryLayer.IsHitTestVisible = false;
        var animation = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(milliseconds))
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