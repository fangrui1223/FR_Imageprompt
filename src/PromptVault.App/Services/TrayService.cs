using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace PromptVault.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private Window _window;

    public TrayService(Window window, Action exit)
    {
        _window = window;
        _ownedIcon = LoadApplicationIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开图库", null, (_, _) => Show());
        menu.Items.Add("退出", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Text = "FR_Imageprompt - 图片提示词收藏器",
            Icon = _ownedIcon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => Show();
    }

    public void UpdateWindow(Window window) => _window = window;

    private void Show()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            return Environment.ProcessPath is { Length: > 0 } path && File.Exists(path)
                ? Icon.ExtractAssociatedIcon(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }
}
