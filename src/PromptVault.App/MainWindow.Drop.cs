using System.Windows;
using System.Windows.Media.Animation;

namespace PromptVault.App;

public partial class MainWindow
{
    private bool _libraryDragActive;
    private DateTime _libraryLastDragOver;

    private void LibraryDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        var source = CaptureWindow.GetDroppedImage(e.Data);
        e.Effects = source is null ? System.Windows.DragDropEffects.None : System.Windows.DragDropEffects.Copy;
        e.Handled = true;
        if (source is null) return;

        _libraryLastDragOver = DateTime.UtcNow;
        if (_libraryDragActive) return;
        _libraryDragActive = true;
        ShowLibraryDropOverlay();
    }

    private async void LibraryDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (!_libraryDragActive) return;
        var observed = _libraryLastDragOver;
        await Task.Delay(100);
        if (!_libraryDragActive || _libraryLastDragOver > observed) return;
        _libraryDragActive = false;
        HideLibraryDropOverlay();
    }

    private async void LibraryDrop(object sender, System.Windows.DragEventArgs e)
    {
        var source = CaptureWindow.GetDroppedImage(e.Data);
        e.Handled = true;
        if (source is null)
        {
            _libraryDragActive = false;
            HideLibraryDropOverlay();
            return;
        }

        _libraryDragActive = false;
        await AnimateLibraryAbsorbAsync();
        await _clipboard.CaptureDroppedImageAsync(source);
    }

    private void ShowLibraryDropOverlay()
    {
        DropOverlay.Visibility = Visibility.Visible;
        DropOverlay.Opacity = 0;
        DropOverlayScale.ScaleX = 0.94;
        DropOverlayScale.ScaleY = 0.94;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DropOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        DropOverlayScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        DropOverlayScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        DropGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(12, 44, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    private void HideLibraryDropOverlay()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (!_libraryDragActive) DropOverlay.Visibility = Visibility.Collapsed;
        };
        DropOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private async Task AnimateLibraryAbsorbAsync()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        DropOverlayScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        DropOverlayScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.72, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        DropGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(44, 4, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        DropOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        await Task.Delay(230);
        DropOverlay.Visibility = Visibility.Collapsed;
    }
}
