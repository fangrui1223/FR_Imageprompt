using System.Runtime.InteropServices;
using System.Windows.Interop;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class BoardWindow
{
    private const int WmKeyDown = 0x0100;
    private const int WmHotKey = 0x0312;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeySpace = 0x20;
    private const int CtrlSpaceHotKeyId = 0x4652;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private HwndSource? _keyboardMessageSource;
    private bool _ctrlSpaceHotKeyRegistered;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _keyboardMessageSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _keyboardMessageSource?.AddHook(BoardKeyboardWndProc);
        Activated += BoardWindowActivatedForHotKey;
        Deactivated += BoardWindowDeactivatedForHotKey;
        RegisterCtrlSpaceHotKey();
    }

    protected override void OnClosed(EventArgs e)
    {
        Activated -= BoardWindowActivatedForHotKey;
        Deactivated -= BoardWindowDeactivatedForHotKey;
        UnregisterCtrlSpaceHotKey();
        if (_keyboardMessageSource is not null)
        {
            _keyboardMessageSource.RemoveHook(BoardKeyboardWndProc);
            _keyboardMessageSource = null;
        }
        base.OnClosed(e);
    }

    private IntPtr BoardKeyboardWndProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == CtrlSpaceHotKeyId)
        {
            handled = true;
            if (_boardBoundaryActive || !IsEnabled) return IntPtr.Zero;
            if (!IsTextEditingFocus()) FocusFullBoard();
            return IntPtr.Zero;
        }

        if (message != WmKeyDown
            || wParam.ToInt32() != VirtualKeySpace
            || (GetKeyState(VirtualKeyControl) & 0x8000) == 0
            || _ctrlSpaceHotKeyRegistered
            || IsTextEditingFocus()) return IntPtr.Zero;

        handled = true;
        var isRepeat = (lParam.ToInt64() & (1L << 30)) != 0;
        if (!isRepeat) FocusFullBoard();
        return IntPtr.Zero;
    }

    private void BoardWindowActivatedForHotKey(object? sender, EventArgs e) =>
        RegisterCtrlSpaceHotKey();

    private void BoardWindowDeactivatedForHotKey(object? sender, EventArgs e) =>
        UnregisterCtrlSpaceHotKey();

    private void RegisterCtrlSpaceHotKey()
    {
        if (_ctrlSpaceHotKeyRegistered || _keyboardMessageSource is null) return;
        _ctrlSpaceHotKeyRegistered = RegisterHotKey(
            _keyboardMessageSource.Handle,
            CtrlSpaceHotKeyId,
            ModControl | ModNoRepeat,
            VirtualKeySpace);
        if (!_ctrlSpaceHotKeyRegistered)
        {
            AppLog.Warning(
                "board-hotkey",
                "Ctrl+Space could not be registered; the routed-key fallback remains active.");
        }
    }

    private void UnregisterCtrlSpaceHotKey()
    {
        if (!_ctrlSpaceHotKeyRegistered || _keyboardMessageSource is null) return;
        UnregisterHotKey(_keyboardMessageSource.Handle, CtrlSpaceHotKeyId);
        _ctrlSpaceHotKeyRegistered = false;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int id,
        uint modifiers,
        int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
