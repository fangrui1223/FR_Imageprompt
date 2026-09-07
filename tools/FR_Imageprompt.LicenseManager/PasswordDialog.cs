using System.Windows;
using System.Windows.Controls;

namespace FR_Imageprompt.LicenseManager;

internal static class PasswordDialog
{
    public static string? Request(Window owner, string title, string prompt, bool confirm)
    {
        var first = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 8, 0, 0) };
        var second = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 8, 0, 0) };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "确定", MinWidth = 90, Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(first);
        if (confirm)
        {
            panel.Children.Add(new TextBlock { Text = "再次输入口令：", Margin = new Thickness(0, 12, 0, 0) });
            panel.Children.Add(second);
        }
        panel.Children.Add(error);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Content = panel,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        ok.Click += (_, _) =>
        {
            if (first.Password.Length < 10) { error.Text = "口令至少需要 10 个字符。"; return; }
            if (confirm && first.Password != second.Password) { error.Text = "两次输入的口令不一致。"; return; }
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true ? first.Password : null;
    }
}
