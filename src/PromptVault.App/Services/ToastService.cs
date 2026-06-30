using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PromptVault.App.Services;

public static class ToastService
{
    public static void Show(Window owner, string message)
    {
        var toast = new Window
        {
            Width = 300, Height = 58, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 19, 31, 45)), CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(86, 214, 255)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = message, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), TextTrimming = TextTrimming.CharacterEllipsis }
            }
        };
        var area = SystemParameters.WorkArea;
        toast.Left = area.Right - toast.Width - 24;
        toast.Top = area.Bottom - toast.Height - 24;
        toast.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); toast.Close(); };
        timer.Start();
    }
}
