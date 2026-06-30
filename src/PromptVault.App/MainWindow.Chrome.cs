using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private bool _oldestFirst;
    private readonly DispatcherTimer _topHideTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly DispatcherTimer _leftHideTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private const double TopPanelMouseSafetyMargin = 4d;
    private bool _leftPanelMenuOpen;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!_trueTransparentWindow) BackdropService.Apply(this);
        _topHideTimer.Tick += (_, _) =>
        {
            _topHideTimer.Stop();
            if (IsMouseInsideTopPanel())
            {
                _topHideTimer.Start();
                return;
            }
            HideTopPanel();
        };
        _leftHideTimer.Tick += (_, _) =>
        {
            _leftHideTimer.Stop();
            if (!_leftPanelMenuOpen && !LeftPanel.IsMouseOver) HideLeftPanel();
        };
        StateChanged += (_, _) => WindowSurface.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(14);
        Loaded += (_, _) => { UpdateTopPanelHeight(); HideTopPanel(); HideLeftPanel(); };
    }

    private void TopPanelMouseEnter(object sender, MouseEventArgs e) { _topHideTimer.Stop(); UpdateTopPanelHeight(); AnimateTop(0); }
    private void TopPanelMouseLeave(object sender, MouseEventArgs e) { _topHideTimer.Stop(); _topHideTimer.Start(); }

    private bool IsMouseInsideTopPanel()
    {
        if (!TopPanel.IsVisible || TopPanel.ActualWidth <= 0 || TopPanel.ActualHeight <= 0) return false;

        var cursor = System.Windows.Forms.Cursor.Position;
        var topLeft = TopPanel.PointToScreen(new Point(0, 0));
        var bottomRight = TopPanel.PointToScreen(new Point(TopPanel.ActualWidth, TopPanel.ActualHeight));
        var left = Math.Min(topLeft.X, bottomRight.X) - TopPanelMouseSafetyMargin;
        var right = Math.Max(topLeft.X, bottomRight.X) + TopPanelMouseSafetyMargin;
        var top = Math.Min(topLeft.Y, bottomRight.Y) - TopPanelMouseSafetyMargin;
        var bottom = Math.Max(topLeft.Y, bottomRight.Y) + TopPanelMouseSafetyMargin;

        return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
    }

    private void LeftPanelMouseEnter(object sender, MouseEventArgs e) { _leftHideTimer.Stop(); AnimateLeft(0); }
    private void LeftPanelMouseLeave(object sender, MouseEventArgs e)
    {
        _leftHideTimer.Stop();
        if (!_leftPanelMenuOpen) _leftHideTimer.Start();
    }

    private void TopControlsWrapSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTopPanelHeight();
    private void HoldLeftPanelForMenu(System.Windows.Controls.ContextMenu menu)
    {
        _leftPanelMenuOpen = true;
        _leftHideTimer.Stop();
        AnimateLeft(0);
        menu.Closed -= SidebarContextMenuClosed;
        menu.Closed += SidebarContextMenuClosed;
    }

    private void SidebarContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        _leftPanelMenuOpen = false;
        _leftHideTimer.Stop();
        if (!LeftPanel.IsMouseOver) _leftHideTimer.Start();
    }

    private void UpdateTopPanelHeight()
    {
        var rows = Math.Clamp((int)Math.Ceiling(Math.Max(58, TopControlsWrap.ActualHeight) / 58d), 1, 3);
        var target = rows switch { 1 => 136d, 2 => 196d, _ => 254d };
        if (Math.Abs(TopPanel.Height - target) < 0.5) return;
        TopPanel.Height = target;
        if (TopPanelTransform.Y < 0) TopPanelTransform.Y = -Math.Max(128, target - 8);
    }

    private void HideTopPanel() => AnimateTop(-Math.Max(128, TopPanel.ActualHeight - 8));
    private void HideLeftPanel() => AnimateLeft(-Math.Max(180, LeftPanel.ActualWidth - 8));
    private void AnimateTop(double value) => TopPanelTransform.BeginAnimation(
        System.Windows.Media.TranslateTransform.YProperty, PanelAnimation(value));

    private void AnimateLeft(double value) => LeftPanelTransform.BeginAnimation(
        System.Windows.Media.TranslateTransform.XProperty, PanelAnimation(value));

    private static DoubleAnimation PanelAnimation(double value) => new(value, TimeSpan.FromMilliseconds(180))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.HoldEnd
    };

    private void DragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (WindowState == WindowState.Normal)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private async void SortButtonClick(object sender, RoutedEventArgs e)
    {
        _oldestFirst = !_oldestFirst;
        SortButton.Content = _oldestFirst ? "最早优先  ↕" : "最新优先  ↕";
        await RefreshAsync();
    }

    private void PinButtonClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        if (PinButton is null) return;
        PinButton.Content = Topmost ? "已置顶" : "置顶";
        PinButton.Background = Topmost
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 118, 153))
            : System.Windows.Media.Brushes.Transparent;
    }
    private void MinimizeButtonClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButtonClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButtonClick(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
