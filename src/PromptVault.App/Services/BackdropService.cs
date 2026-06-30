using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PromptVault.App.Services;

public static class BackdropService
{
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var enabled = 1;
            var rounded = 2;
            var mica = 2;
            DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
            DwmSetWindowAttribute(handle, 38, ref mica, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
