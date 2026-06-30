using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PromptVault.App;

public partial class MainWindow
{
    private bool _transparentMode;
    private bool _ctrlRightDragging;
    private Point _ctrlRightStartScreen;
    private Point _ctrlRightStartWindow;

    private void TransparentToggleClick(object sender, RoutedEventArgs e) => ToggleTransparentMode();

    private void ToggleTransparentMode()
    {
        if (System.Windows.Application.Current is App app) app.SwitchMainWindow(!_transparentMode, CreateSnapshot());
    }

    private void ApplyTransparentMode()
    {
        var transparent = _transparentMode;
        System.Windows.Application.Current.Resources["CardSurfaceBrush"] = Brush(transparent ? Colors.Transparent : Color.FromRgb(18, 26, 37));
        System.Windows.Application.Current.Resources["CardBorderBrush"] = Brush(transparent ? Colors.Transparent : Color.FromRgb(38, 57, 77));
        System.Windows.Application.Current.Resources["ImageWellBrush"] = Brush(transparent ? Colors.Transparent : Color.FromRgb(7, 11, 17));
        System.Windows.Application.Current.Resources["CardLabelBrush"] = Brush(transparent ? Colors.Transparent : Color.FromArgb(225, 16, 24, 35));
        System.Windows.Application.Current.Resources["CardTextBrush"] = Brush(transparent ? Colors.Transparent : Color.FromRgb(238, 246, 255));
        System.Windows.Application.Current.Resources["CardMetaTextBrush"] = Brush(transparent ? Colors.Transparent : Color.FromRgb(130, 146, 166));
        System.Windows.Application.Current.Resources["HeaderHintBrush"] = Brush(transparent ? Colors.Transparent : Color.FromRgb(83, 103, 125));
        System.Windows.Application.Current.Resources["StatusHintBrush"] = Brush(transparent ? Colors.Transparent : Color.FromRgb(130, 146, 166));

        WindowSurface.Background = transparent ? Brushes.Transparent : Brush(Color.FromArgb(242, 10, 16, 25));
        WindowSurface.BorderBrush = transparent ? Brushes.Transparent : Brush(Color.FromArgb(49, 68, 90, 112));
        TopPanel.Background = transparent ? Brushes.Transparent : CreateTopPanelBrush();
        TopPanel.BorderBrush = transparent ? Brushes.Transparent : Brush(Color.FromArgb(74, 83, 108, 134));
        TopPanel.Effect = transparent ? null : new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.45 };
        LeftPanel.Background = transparent ? Brushes.Transparent : CreateLeftPanelBrush();
        LeftPanel.BorderBrush = transparent ? Brushes.Transparent : Brush(Color.FromArgb(74, 83, 108, 134));
        LeftPanel.Effect = transparent ? null : new DropShadowEffect { BlurRadius = 34, ShadowDepth = 9, Direction = 0, Opacity = 0.5 };
        ImmersiveViewer.Background = transparent ? Brushes.Transparent : Brush(Color.FromArgb(252, 7, 12, 19));
        StatusText.Opacity = transparent ? 0 : 1;

        ApplyTextBoxChrome(SearchBox, transparent);
        ApplyTextBoxChrome(TagBox, transparent);
        TransparentToggleButton.Content = transparent ? "退出透明" : "透明";
        if (!transparent)
        {
            ApplyTransparentButtonChrome(TopPanel, false);
            ApplyTransparentButtonChrome(LeftPanel, false);
        }

        UpdateCaptureToggleVisual();
        UpdateSelectionVisual();
        UpdateTrashVisual();
        UpdatePinVisual();

        if (transparent)
        {
            ApplyTransparentButtonChrome(TopPanel, true);
            ApplyTransparentButtonChrome(LeftPanel, true);
            TransparentToggleButton.Background = Brush(Color.FromArgb(120, 86, 214, 255));
            TransparentToggleButton.BorderBrush = Brush(Color.FromRgb(86, 214, 255));
        }
    }

    private bool TryStartCtrlRightDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return false;
        _ctrlRightDragging = true;
        _ctrlRightStartScreen = PointToScreen(e.GetPosition(this));
        _ctrlRightStartWindow = new Point(Left, Top);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
        return true;
    }

    private void MainWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_ctrlRightDragging) return;
        var current = PointToScreen(e.GetPosition(this));
        Left = _ctrlRightStartWindow.X + current.X - _ctrlRightStartScreen.X;
        Top = _ctrlRightStartWindow.Y + current.Y - _ctrlRightStartScreen.Y;
        e.Handled = true;
    }

    private void MainWindowPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_ctrlRightDragging) return;
        _ctrlRightDragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private static System.Windows.Media.Brush Brush(Color color) => new SolidColorBrush(color);

    private static void ApplyTextBoxChrome(TextBox textBox, bool transparent)
    {
        textBox.Background = transparent ? Brushes.Transparent : Brush(Color.FromArgb(117, 16, 26, 38));
        textBox.BorderBrush = transparent ? Brushes.Transparent : Brush(Color.FromRgb(63, 82, 106));
    }

    private static System.Windows.Media.Brush CreateTopPanelBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(239, 17, 27, 40), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(223, 26, 41, 58), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(233, 14, 23, 35), 1));
        return brush;
    }

    private static System.Windows.Media.Brush CreateLeftPanelBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(240, 17, 28, 41), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 27, 42, 59), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(234, 13, 22, 34), 1));
        return brush;
    }

    private static void ApplyTransparentButtonChrome(DependencyObject root, bool transparent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
            {
                button.Background = transparent ? Brushes.Transparent : Brush(Color.FromRgb(29, 42, 59));
                button.BorderBrush = transparent ? Brushes.Transparent : Brush(Color.FromRgb(53, 74, 97));
                button.Foreground = Brush(Color.FromRgb(238, 246, 255));
            }
            ApplyTransparentButtonChrome(child, transparent);
        }
    }
}