using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PromptVault.App;

public sealed class CategoryDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _description = new();
    public string CategoryName => _name.Text.Trim();
    public string Description => _description.Text.Trim();

    public CategoryDialog()
    {
        Title = "新建分类";
        Width = 460;
        Height = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var shell = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(246, 14, 22, 33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 95, 119)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.45 }
        };

        var root = new Grid { Margin = new Thickness(24, 20, 24, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        header.Children.Add(new TextBlock
        {
            Text = "新建分类",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255)),
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button { Content = "×", Width = 40, Height = 34, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(27, 42, 59)) };
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var form = new StackPanel();
        Grid.SetRow(form, 1);
        form.Children.Add(Label("分类名称"));
        _name.Height = 48;
        _name.FontSize = 16;
        _name.VerticalContentAlignment = VerticalAlignment.Center;
        form.Children.Add(_name);
        form.Children.Add(Label("AI 描述（可选）", 18));
        _description.Height = 68;
        _description.FontSize = 15;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.AcceptsReturn = true;
        form.Children.Add(_description);
        form.Children.Add(new TextBlock
        {
            Text = "例如：动漫、二次元、日系插画、美女人像、角色立绘",
            Foreground = new SolidColorBrush(Color.FromRgb(130, 146, 166)),
            Margin = new Thickness(0, 8, 0, 0)
        });
        root.Children.Add(form);

        var footer = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        Grid.SetRow(footer, 2);
        var cancel = new Button { Content = "取消", MinWidth = 88, Height = 40, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var create = new Button { Content = "创建", MinWidth = 92, Height = 40, IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(86, 214, 255)), Foreground = new SolidColorBrush(Color.FromRgb(5, 15, 24)) };
        create.Click += (_, _) => { if (CategoryName.Length > 0) DialogResult = true; };
        footer.Children.Add(cancel);
        footer.Children.Add(create);
        root.Children.Add(footer);

        shell.Child = root;
        Content = shell;
        Loaded += (_, _) => _name.Focus();
    }

    private static TextBlock Label(string text, double top = 0) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255)),
        Margin = new Thickness(0, top, 0, 7)
    };
}