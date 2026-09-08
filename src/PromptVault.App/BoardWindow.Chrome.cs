using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private readonly DispatcherTimer _boardTopRevealTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _boardTopHideTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _boardStatusHideTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private bool _boardTopBarShown;
    private bool _boardTopBarKeyboardHeld;
    private bool _pointerInsideTopBar;
    private bool _pointerInsideTopEdge;
    private bool _boardChromeRetired;

    private bool IsBoardChromeAvailable => !_boardChromeRetired
        && PresentationSource.FromVisual(this) is HwndSource { IsDisposed: false, RootVisual: not null };

    private void RetireBoardChrome()
    {
        _boardChromeRetired = true;
        _boardTopBarShown = false;
        _boardTopBarKeyboardHeld = false;
        _pointerInsideTopBar = false;
        _pointerInsideTopEdge = false;
        _boardTopRevealTimer.Stop();
        _boardTopHideTimer.Stop();
        _boardStatusHideTimer.Stop();
        _boardTopRevealTimer.Tick -= BoardTopRevealTimerTick;
        _boardTopHideTimer.Tick -= BoardTopHideTimerTick;
        _boardStatusHideTimer.Tick -= BoardStatusHideTimerTick;
    }

    private void InitializeBoardChrome()
    {
        if (_boardChromeRetired) return;
        _boardTopRevealTimer.Tick -= BoardTopRevealTimerTick;
        _boardTopRevealTimer.Tick += BoardTopRevealTimerTick;
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
        if (!IsBoardChromeAvailable) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed
            || Mouse.MiddleButton == MouseButtonState.Pressed) return;
        _pointerInsideTopEdge = true;
        if (!_boardTopBarShown)
        {
            _boardTopRevealTimer.Stop();
            _boardTopRevealTimer.Start();
        }
    }

    private void TopEdgeMouseLeave(object sender, MouseEventArgs e)
    {
        _pointerInsideTopEdge = false;
        _boardTopRevealTimer.Stop();
        ScheduleTopBarHide();
    }

    private void BoardWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsBoardChromeAvailable) return;
        UpdateTopBarPointerIntent(e.GetPosition(BoardRoot),
            e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released
            && e.MiddleButton == MouseButtonState.Released);
    }

    private void UpdateTopBarPointerIntent(Point position, bool buttonsReleased)
    {
        if (!IsBoardChromeAvailable) return;
        var insideSensor = position.X >= 0 && position.X <= BoardRoot.ActualWidth
            && position.Y >= 0 && position.Y <= TopEdgeActivationZone.ActualHeight
            && buttonsReleased;
        if (insideSensor)
        {
            _pointerInsideTopEdge = true;
            if (!_boardTopBarShown && !_boardTopRevealTimer.IsEnabled) _boardTopRevealTimer.Start();
            return;
        }
        _pointerInsideTopEdge = false;
        _boardTopRevealTimer.Stop();
        if (!_boardTopBarShown) return;
        _pointerInsideTopBar = IsPointerInsideTopBar();
        if (_pointerInsideTopBar || IsPointerInsideTopSafeCorridor())
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
        if (!IsBoardChromeAvailable) return;
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
        if (!IsBoardChromeAvailable) return;
        if (!_boardTopBarShown || _boardTopBarKeyboardHeld) return;
        if (_boardTopHideTimer.IsEnabled) return;
        _boardTopHideTimer.Start();
    }

    private void BoardTopRevealTimerTick(object? sender, EventArgs e)
    {
        _boardTopRevealTimer.Stop();
        if (!TryGetChromePointerPosition(out var point)) return;
        if (_pointerInsideTopEdge && IsActive && point.X >= 0 && point.X <= BoardRoot.ActualWidth
            && point.Y >= 0 && point.Y <= TopEdgeActivationZone.ActualHeight
            && System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.None)
            ShowBoardTopBar(focusKeyboard: false);
    }

    private void BoardTopHideTimerTick(object? sender, EventArgs e)
    {
        if (!IsBoardChromeAvailable)
        {
            _boardTopHideTimer.Stop();
            return;
        }
        _pointerInsideTopBar = IsPointerInsideTopBar();
        if (_boardTopBarKeyboardHeld || _pointerInsideTopBar
            || IsPointerInsideTopSafeCorridor()
            || BoardTopBar.IsKeyboardFocusWithin
            || BoardSelector.IsDropDownOpen
            || NoteColorPopup.IsOpen
            || _openBoardContextMenu?.IsOpen == true) return;
        _boardTopHideTimer.Stop();
        HideBoardTopBar();
    }

    private bool IsPointerInsideTopBar()
    {
        if (!IsBoardChromeAvailable || !BoardTopBar.IsVisible || !BoardTopBar.IsHitTestVisible) return false;
        var point = Mouse.GetPosition(BoardTopBar);
        return point.X >= 0 && point.Y >= 0
            && point.X <= BoardTopBar.ActualWidth
            && point.Y <= BoardTopBar.ActualHeight;
    }

    private bool IsPointerInsideTopSafeCorridor()
    {
        if (!TryGetChromePointerPosition(out var point)) return false;
        return point.X >= 0
            && point.X <= BoardRoot.ActualWidth
            && point.Y >= 0
            && point.Y <= BoardTopBar.Margin.Top + BoardTopBar.ActualHeight + 8;
    }

    private bool TryGetChromePointerPosition(out Point position)
    {
        position = default;
        // IsLoaded can remain true after HWND destruction; the presentation source is authoritative.
        if (!IsBoardChromeAvailable) return false;
        var point = System.Windows.Forms.Cursor.Position;
        position = PointFromScreen(new Point(point.X, point.Y));
        return true;
    }

    private void HandleChromePointerMessage(int message)
    {
        // WindowChrome owns the outer 8 DIP resize band, so WPF mouse events alone miss it.
        if (message is not (0x00A0 or 0x02A2)) return; // WM_NCMOUSEMOVE / WM_NCMOUSELEAVE
        if (!TryGetChromePointerPosition(out var position)) return;
        UpdateTopBarPointerIntent(position,
            System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.None);
        if (message == 0x00A0 && _keyboardMessageSource is not null)
        {
            var tracking = new ChromeMouseTracking
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ChromeMouseTracking>(),
                Flags = 0x00000002 | 0x00000010, // TME_LEAVE | TME_NONCLIENT
                Window = _keyboardMessageSource.Handle
            };
            TrackMouseEvent(ref tracking);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ChromeMouseTracking
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref ChromeMouseTracking tracking);

    private void ShowBoardTopBar(bool focusKeyboard)
    {
        if (!IsBoardChromeAvailable) return;
        _boardTopRevealTimer.Stop();
        _boardTopHideTimer.Stop();
        _boardTopBarShown = true;
        BoardTopBar.IsHitTestVisible = true;
        AnimateTopBar(1, 0);
        if (focusKeyboard) BoardSelector.Focus();
    }

    private void HideBoardTopBar()
    {
        _boardTopRevealTimer.Stop();
        _boardTopHideTimer.Stop();
        _boardTopBarKeyboardHeld = false;
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

    private void ShowTopBarFromKeyboard(bool isRepeat = false)
    {
        if (isRepeat || !IsBoardChromeAvailable) return;
        if (_boardTopBarShown)
        {
            HideBoardTopBar();
            SetStatus("顶部栏已隐藏");
        }
        else
        {
            ShowBoardTopBar(focusKeyboard: false);
            _boardTopBarKeyboardHeld = true;
            SetStatus("顶部栏已显示");
        }
    }

    private void BoardWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _boardTopRevealTimer.Stop();
        if (!_boardTopBarShown || e.OriginalSource is not DependencyObject source) return;
        if (IsInputDescendantOf(source, BoardViewport))
        {
            if (BoardTopBar.IsKeyboardFocusWithin) Keyboard.Focus(BoardViewport);
            HideBoardTopBar();
        }
    }

    private void ShowStatusOverlay()
    {
        if (_boardChromeRetired) return;
        BoardStatusOverlay.Opacity = 1;
        _boardStatusHideTimer.Stop();
        BoardStatusOverlay.IsHitTestVisible = _saveError is not null;
        if (_saveError is null) _boardStatusHideTimer.Start();
    }

    private void BoardStatusHideTimerTick(object? sender, EventArgs e)
    {
        _boardStatusHideTimer.Stop();
        BoardStatusOverlay.Opacity = 0;
    }
}
