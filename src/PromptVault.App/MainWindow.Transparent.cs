using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.App.Services;

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
        VisualModeService.Apply(transparent, _settings.ReducedMotionEnabled);
        WindowSurface.Background = VisualModeService.ResourceBrush("WindowSurfaceBrush");
        WindowSurface.BorderBrush = VisualModeService.ResourceBrush("WindowBorderBrush");
        TopPanel.Background = VisualModeService.ResourceBrush("TopPanelBrush");
        TopPanel.BorderBrush = transparent ? Brushes.Transparent : VisualModeService.ResourceBrush("HairlineBrush");
        TopPanel.Effect = VisualModeService.ResourceEffect("FloatingPanelShadow");
        LeftPanel.Background = VisualModeService.ResourceBrush("LeftPanelBrush");
        LeftPanel.BorderBrush = transparent ? Brushes.Transparent : VisualModeService.ResourceBrush("HairlineBrush");
        LeftPanel.Effect = VisualModeService.ResourceEffect("SidePanelShadow");
        ImmersiveViewer.Background = VisualModeService.ResourceBrush("ImmersiveBackdropBrush");
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
            TransparentToggleButton.Background = VisualModeService.ResourceBrush("AccentSoftBrush");
            TransparentToggleButton.BorderBrush = VisualModeService.ResourceBrush("AccentBrush");
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

    private static void ApplyTextBoxChrome(TextBox textBox, bool transparent)
    {
        textBox.Background = transparent
            ? Brushes.Transparent
            : VisualModeService.ResourceBrush("TextFieldBrush");
        textBox.BorderBrush = transparent
            ? Brushes.Transparent
            : VisualModeService.ResourceBrush("TextFieldBorderBrush");
    }

    private static void ApplyTransparentButtonChrome(DependencyObject root, bool transparent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
            {
                button.Background = transparent
                    ? Brushes.Transparent
                    : VisualModeService.ResourceBrush("ButtonSurfaceBrush");
                button.BorderBrush = transparent
                    ? Brushes.Transparent
                    : VisualModeService.ResourceBrush("ButtonBorderBrush");
                button.Foreground = VisualModeService.ResourceBrush("PrimaryTextBrush");
            }
            ApplyTransparentButtonChrome(child, transparent);
        }
    }
}
