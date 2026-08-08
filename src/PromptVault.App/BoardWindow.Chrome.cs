using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private readonly DispatcherTimer _boardTopHideTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _boardStatusHideTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private bool _boardTopBarShown;
    private bool _pointerInsideTopBar;

    private void InitializeBoardChrome()
    {
        _boardTopHideTimer.Tick -= BoardTopHideTimerTick;
        _boardTopHideTimer.Tick += BoardTopHideTimerTick;
        _boardStatusHideTimer.Tick -= BoardStatusHideTimerTick;
        _boardStatusHideTimer.Tick += BoardStatusHideTimerTick;
        ApplyTopmostPreference(_settings.BoardAlwaysOnTop);
        UpdateMaximizeButton();
        BoardStatusOverlay.Opacity = 0;
    }

    internal void ApplyTopmostPreference(bool value)
    {
        Topmost = value;
        if (TopmostButton is not null) TopmostButton.IsChecked = value;
        RefreshOpenContextMenuState();
    }

    private void ToggleTopmostPreference()
    {
        var value = !Topmost;
        _workspace.SetAlwaysOnTop(value);
        SetStatus(value ? "画板已置顶" : "已取消置顶");
    }

    private async void TopmostButtonClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.ToggleTopmost);

    private void MinimizeWindowClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindowClick(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void BoardWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizeButton();

    private void UpdateMaximizeButton()
    {
        if (MaximizeButton is null) return;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    private void TopBarTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        e.Handled = true;
    }

    private void TopEdgeMouseEnter(object sender, MouseEventArgs e)
    {
        ShowBoardTopBar(focusKeyboard: false);
    }

    private void TopEdgeMouseLeave(object sender, MouseEventArgs e)
    {
        ScheduleTopBarHide();
    }

    private void BoardWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(BoardRoot);
        if (position.Y >= 0 && position.Y <= Math.Max(28, TopEdgeActivationZone.ActualHeight))
        {
            if (!_boardTopBarShown) ShowBoardTopBar(focusKeyboard: false);
            return;
        }
        if (!_boardTopBarShown) return;
        _pointerInsideTopBar = IsPointerInsideTopBar();
        if (_pointerInsideTopBar)
        {
            _boardTopHideTimer.Stop();
        }
        else if (!_boardTopHideTimer.IsEnabled)
        {
            ScheduleTopBarHide();
        }
    }

    private void BoardTopBarMouseEnter(object sender, MouseEventArgs e)
    {
        _pointerInsideTopBar = true;
        _boardTopHideTimer.Stop();
    }

    private void BoardTopBarMouseLeave(object sender, MouseEventArgs e)
    {
        _pointerInsideTopBar = false;
        ScheduleTopBarHide();
    }

    private void ScheduleTopBarHide()
    {
        if (!_boardTopBarShown) return;
        if (_boardTopHideTimer.IsEnabled) return;
        _boardTopHideTimer.Start();
    }

    private void BoardTopHideTimerTick(object? sender, EventArgs e)
    {
        _pointerInsideTopBar = IsPointerInsideTopBar();
        if (_pointerInsideTopBar
            || BoardSelector.IsDropDownOpen
            || _openBoardContextMenu?.IsOpen == true) return;
        _boardTopHideTimer.Stop();
        HideBoardTopBar();
    }

    private bool IsPointerInsideTopBar()
    {
        if (!BoardTopBar.IsVisible || !BoardTopBar.IsHitTestVisible) return false;
        var point = Mouse.GetPosition(BoardTopBar);
        return point.X >= 0 && point.Y >= 0
            && point.X <= BoardTopBar.ActualWidth
            && point.Y <= BoardTopBar.ActualHeight;
    }

    private void ShowBoardTopBar(bool focusKeyboard)
    {
        _boardTopHideTimer.Stop();
        _boardTopBarShown = true;
        BoardTopBar.IsHitTestVisible = true;
        AnimateTopBar(1, 0);
        if (focusKeyboard) BoardSelector.Focus();
    }

    private void HideBoardTopBar()
    {
        _boardTopBarShown = false;
        BoardTopBar.IsHitTestVisible = false;
        AnimateTopBar(0, -80);
    }

    private void AnimateTopBar(double opacity, double translateY)
    {
        var duration = _settings.ReducedMotionEnabled
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(120);
        BoardTopBar.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(opacity, new Duration(duration)) { FillBehavior = FillBehavior.HoldEnd });
        BoardTopBarTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(translateY, new Duration(duration)) { FillBehavior = FillBehavior.HoldEnd });
    }

    private void ShowTopBarFromKeyboard()
    {
        ShowBoardTopBar(focusKeyboard: false);
        ScheduleTopBarHide();
        SetStatus("顶部栏已显示");
    }

    private void ShowStatusOverlay()
    {
        BoardStatusOverlay.Opacity = 1;
        _boardStatusHideTimer.Stop();
        _boardStatusHideTimer.Start();
    }

    private void BoardStatusHideTimerTick(object? sender, EventArgs e)
    {
        _boardStatusHideTimer.Stop();
        BoardStatusOverlay.Opacity = 0;
    }
}
