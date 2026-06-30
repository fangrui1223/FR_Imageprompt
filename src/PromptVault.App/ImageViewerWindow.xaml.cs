using System.Windows;
using System.Windows.Input;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class ImageViewerWindow : Window
{
    private bool _dragging;
    private Point _lastPoint;

    public ImageViewerWindow(string path)
    {
        InitializeComponent();
        ViewerImage.Source = ImagePipeline.LoadPreview(path, 0);
    }

    private void ViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.14 : 1 / 1.14;
        var scale = Math.Clamp(ImageScale.ScaleX * factor, 0.5, 12);
        ImageScale.ScaleX = ImageScale.ScaleY = scale;
        if (scale <= 1) ImageTranslate.X = ImageTranslate.Y = 0;
    }

    private void ViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true; _lastPoint = e.GetPosition(this); CaptureMouse(); Cursor = Cursors.SizeAll;
    }

    private void ViewerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || ImageScale.ScaleX <= 1) return;
        var point = e.GetPosition(this);
        ImageTranslate.X += point.X - _lastPoint.X; ImageTranslate.Y += point.Y - _lastPoint.Y; _lastPoint = point;
    }

    private void ViewerMouseUp(object sender, MouseButtonEventArgs e) { _dragging = false; ReleaseMouseCapture(); Cursor = Cursors.Arrow; }
    private void WindowKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); if (e.Key == Key.D0) Reset(); }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void Reset() { ImageScale.ScaleX = ImageScale.ScaleY = 1; ImageTranslate.X = ImageTranslate.Y = 0; }
}
