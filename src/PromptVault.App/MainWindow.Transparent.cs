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
    private DpiScale _ctrlRightDpi;
    private bool _transparentToggleActive;

    private void TransparentToggleClick(object sender, RoutedEventArgs e) => ToggleTransparentMode();

    private async void ToggleTransparentMode()
    {
        if (_transparentToggleActive || _handoffInputFrozen) return;
        _transparentToggleActive = true;
        try
        {
            if (!_transparentMode && _inspectorVisible)
            {
                if (!await SaveInspectorOrdinaryFieldsAsync())
                {
                    InspectorTagsEditor.Focus();
                    return;
                }
                if (!await ResolveDirtyPromptBeforeSwitchAsync())
                {
                    InspectorPromptEditor.Focus();
                    return;
                }
            }
            if (System.Windows.Application.Current is App app)
                await app.SwitchMainWindowAsync(this, !_transparentMode);
        }
        finally { _transparentToggleActive = false; }
    }

    private void ApplyTransparentMode(bool applyVisualModeResources = true)
    {
        _transparentModeApplyCount++;
        var transparent = _transparentMode;
        if (applyVisualModeResources)
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
        GalleryHeader.Visibility = Visibility.Visible;
        GalleryHeader.Opacity = transparent ? 0 : 1;
        GalleryHeader.IsHitTestVisible = !transparent;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Opacity = transparent ? 0 : 1;
        GalleryLayer.Margin = (Thickness)FindResource("SpacingWindow");
        if (_inspectorVisible)
        {
            InspectorPanel.Visibility = transparent ? Visibility.Hidden : Visibility.Visible;
            InspectorPanel.IsHitTestVisible = !transparent;
            InspectorPanel.Opacity = transparent ? 0 : 1;
            InspectorSplitter.Visibility = transparent ? Visibility.Hidden : Visibility.Visible;
        }

        ApplyTextBoxChrome(SearchBox, transparent);
        ApplyTextBoxChrome(TagBox, transparent);
        ApplyTextBoxChrome(CommandSearchBox, transparent);
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
            HideTopPanel();
            HideLeftPanel();
            ApplyTransparentButtonChrome(TopPanel, true);
            ApplyTransparentButtonChrome(LeftPanel, true);
            TransparentToggleButton.Background = VisualModeService.ResourceBrush("AccentSoftBrush");
            TransparentToggleButton.BorderBrush = VisualModeService.ResourceBrush("AccentBrush");
        }
        else
        {
            ApplyEdgeMenuPreference();
        }
    }

    private bool TryStartCtrlRightDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return false;
        if (WindowState != WindowState.Normal || _handoffInputFrozen) return false;
        _ctrlRightDragging = true;
        _ctrlRightStartScreen = PointToScreen(e.GetPosition(this));
        _ctrlRightStartWindow = new Point(Left, Top);
        _ctrlRightDpi = VisualTreeHelper.GetDpi(this);
        if (!CaptureMouse()) { _ctrlRightDragging = false; return false; }
        Cursor = Cursors.SizeAll;
        e.Handled = true;
        return true;
    }

    private void MainWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        ObserveEdgeIntent(e.GetPosition(this));
        if (!_ctrlRightDragging) return;
        if (e.RightButton != MouseButtonState.Pressed || Mouse.Captured != this)
        {
            EndCtrlRightDrag();
            return;
        }
        var current = PointToScreen(e.GetPosition(this));
        var position = WindowDragGeometry.Position(_ctrlRightStartWindow, _ctrlRightStartScreen, current, _ctrlRightDpi);
        Left = position.X;
        Top = position.Y;
        e.Handled = true;
    }

    private void MainWindowPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_ctrlRightDragging) return;
        EndCtrlRightDrag();
        e.Handled = true;
    }

    private void EndCtrlRightDrag()
    {
        _ctrlRightDragging = false;
        if (Mouse.Captured == this) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
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
