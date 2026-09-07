using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PromptVault.App;

public partial class MainWindow
{
    internal bool VerifyM11FrozenInput()
    {
        if (!_handoffInputFrozen || !_galleryRowsTransferredOut) return false;
        var offset = _rowsScrollViewer?.VerticalOffset;
        var selected = _selectedItemIds.Order().ToArray();
        var handle = new WindowInteropHelper(this).Handle;
        M11SendMessage(handle, 0x020A, new IntPtr(120 << 16), IntPtr.Zero);
        M11SendMessage(handle, 0x0201, new IntPtr(1), new IntPtr((300 << 16) | 200));
        M11SendMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
        return IsVisible && offset == _rowsScrollViewer?.VerticalOffset && selected.SequenceEqual(_selectedItemIds.Order());
    }

    internal bool VerifyM11CaptureInterruption()
    {
        if (!CaptureMouse()) return false;
        _ctrlRightDragging = true;
        Cursor = Cursors.SizeAll;
        ReleaseMouseCapture();
        return !_ctrlRightDragging && Cursor == Cursors.Arrow;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr M11SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
