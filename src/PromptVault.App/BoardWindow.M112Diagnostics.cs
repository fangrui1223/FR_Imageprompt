using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task<Dictionary<string, bool>> RunM112CloseSmokeAsync(bool keyboardOpened)
    {
        var results = new Dictionary<string, bool>();
        EnsureIsolatedM8Settings(_settings, "M11.2 关闭回归");
        await EnsureBoardLoadedAsync();
        if (keyboardOpened) ShowTopBarFromKeyboard();
        else ShowBoardTopBar(focusKeyboard: false);
        _boardTopRevealTimer.Start();
        _boardTopHideTimer.Start();
        ShowStatusOverlay();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => closed.TrySetResult();
        Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        results["ClosedAndSourceDetached"] = PresentationSource.FromVisual(this) is null;
        results["AllChromeTimersStopped"] = TimersStopped();
        // WPF input already queued before HWND teardown can arrive after Closed.
        Probe("LateTopEdgeLeave", () => TopEdgeMouseLeave(this, new MouseEventArgs(Mouse.PrimaryDevice, 0)));
        Probe("LateTopBarLeave", () => BoardTopBarMouseLeave(this, new MouseEventArgs(Mouse.PrimaryDevice, 0)));
        Probe("LatePointerMove", () => UpdateTopBarPointerIntent(new Point(100, 100), true));
        Probe("LateNativeLeave", () => HandleChromePointerMessage(0x02A2));
        Probe("LateRevealTick", () => BoardTopRevealTimerTick(null, EventArgs.Empty));
        Probe("LateHideTick", () => BoardTopHideTimerTick(null, EventArgs.Empty));
        Probe("LateStatus", ShowStatusOverlay);
        Probe("LateKeyboardReveal", () => ShowTopBarFromKeyboard());
        Probe("LateInitialization", InitializeBoardChrome);
        results["ClosedStateCannotReactivate"] = _boardChromeRetired && !_boardTopBarShown
            && !_boardTopBarKeyboardHeld && !TryGetChromePointerPosition(out _);
        await Task.Delay(650);
        results["StillStoppedAfterHideDeadline"] = TimersStopped();
        return results;

        bool TimersStopped() => !_boardTopRevealTimer.IsEnabled && !_boardTopHideTimer.IsEnabled
            && !_boardStatusHideTimer.IsEnabled;
        void Probe(string name, Action action)
        {
            try { action(); results[name] = TimersStopped(); }
            catch (InvalidOperationException) { results[name] = false; }
            finally
            {
                // Keep a failing diagnostic from scheduling an unhandled exception in its host.
                _boardTopRevealTimer.Stop();
                _boardTopHideTimer.Stop();
                _boardStatusHideTimer.Stop();
            }
        }
    }
}
