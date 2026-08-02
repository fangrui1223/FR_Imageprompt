using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private readonly EdgeIntentDetector _boardTopEdgeIntent = new();
    private readonly Stopwatch _boardChromeClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _boardTopEdgeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _boardTopHideTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _boardStatusHideTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private bool _boardTopBarShown;
    private bool _pointerInsideTopBar;

    private void InitializeBoardChrome()
    {
        _boardTopEdgeTimer.Tick -= BoardTopEdgeTimerTick;
        _boardTopEdgeTimer.Tick += BoardTopEdgeTimerTick;
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
        _boardTopEdgeIntent.Reset();
        _boardTopEdgeTimer.Start();
    }

    private void TopEdgeMouseLeave(object sender, MouseEventArgs e)
    {
        _boardTopEdgeTimer.Stop();
        _boardTopEdgeIntent.Reset();
        ScheduleTopBarHide();
    }

    private void BoardWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_boardTopBarShown || !_boardTopEdgeTimer.IsEnabled) return;
        PollBoardTopEdgeIntent();
    }

    private void BoardTopEdgeTimerTick(object? sender, EventArgs e) => PollBoardTopEdgeIntent();

    private void PollBoardTopEdgeIntent()
    {
        var point = Mouse.GetPosition(this);
        var profile = EdgeIntentProfile.FromSensitivity(_settings.EdgeMenuSensitivity);
        if (_boardTopEdgeIntent.Poll(
                point.X,
                point.Y,
                Math.Max(1, ActualWidth),
                Math.Max(1, ActualHeight),
                _boardChromeClock.Elapsed,
                profile) != EdgeIntentEdge.Top) return;
        _boardTopEdgeTimer.Stop();
        ShowBoardTopBar(focusKeyboard: false);
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
        _boardTopHideTimer.Stop();
        _boardTopHideTimer.Start();
    }

    private void BoardTopHideTimerTick(object? sender, EventArgs e)
    {
        if (_pointerInsideTopBar
            || BoardTopBar.IsKeyboardFocusWithin
            || BoardSelector.IsDropDownOpen
            || _openBoardContextMenu?.IsOpen == true) return;
        _boardTopHideTimer.Stop();
        HideBoardTopBar();
    }

    private void ShowBoardTopBar(bool focusKeyboard)
    {
        _boardTopBarShown = true;
        BoardTopBar.IsHitTestVisible = true;
        AnimateTopBar(1, 0);
        if (focusKeyboard) BoardSelector.Focus();
    }

    private void HideBoardTopBar()
    {
        _boardTopBarShown = false;
        BoardTopBar.IsHitTestVisible = false;
        AnimateTopBar(0, -74);
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
        ShowBoardTopBar(focusKeyboard: true);
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
