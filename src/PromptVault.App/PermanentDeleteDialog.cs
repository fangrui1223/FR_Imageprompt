using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PromptVault.App;

public sealed class PermanentDeleteDialog : Window
{
    public PermanentDeleteDialog(int itemCount)
    {
        Title = "\u6C38\u4E45\u5220\u9664";
        Width = 480;
        Height = 286;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var shell = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(248, 14, 22, 33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 95, 119)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 9, Opacity = 0.5 }
        };

        var root = new Grid { Margin = new Thickness(24, 20, 24, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }
        };

        var titleBlock = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        titleBlock.Children.Add(new TextBlock
        {
            Text = "\u6C38\u4E45\u5220\u9664",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255))
        });
        titleBlock.Children.Add(new TextBlock
        {
            Text = "\u8FD9\u4E2A\u64CD\u4F5C\u4E0D\u53EF\u6062\u590D",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 146, 166))
        });
        header.Children.Add(titleBlock);

        var close = new Button
        {
            Content = "\u00D7",
            Width = 40,
            Height = 34,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(27, 42, 59))
        };
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);

        var iconShell = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Color.FromArgb(55, 255, 100, 100)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 124, 124)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 18, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        iconShell.Child = new TextBlock
        {
            Text = "!",
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 151, 151)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        body.Children.Add(iconShell);

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(copy, 1);
        copy.Children.Add(new TextBlock
        {
            Text = itemCount == 1 ? "\u786E\u5B9A\u8981\u6C38\u4E45\u5220\u9664\u8FD9\u5F20\u56FE\u7247\u5417\uFF1F" : $"\u786E\u5B9A\u8981\u6C38\u4E45\u5220\u9664 {itemCount} \u5F20\u56FE\u7247\u5417\uFF1F",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255)),
            TextWrapping = TextWrapping.Wrap
        });
        copy.Children.Add(new TextBlock
        {
            Text = "\u56FE\u7247\u8BB0\u5F55\u548C\u56FE\u5E93\u6587\u4EF6\u5C06\u88AB\u5220\u9664\uFF0C\u4E4B\u540E\u65E0\u6CD5\u4ECE\u56DE\u6536\u7AD9\u6062\u590D\u3002",
            Margin = new Thickness(0, 9, 0, 0),
            FontSize = 14,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(154, 170, 188)),
            TextWrapping = TextWrapping.Wrap
        });
        copy.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 18, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(40, 60, 78))
        });
        body.Children.Add(copy);
        root.Children.Add(body);

        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        Grid.SetRow(footer, 2);

        var cancel = new Button
        {
            Content = "\u53D6\u6D88",
            MinWidth = 96,
            Height = 40,
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true,
            Background = new SolidColorBrush(Color.FromRgb(29, 42, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(73, 88, 106))
        };
        cancel.Click += (_, _) => DialogResult = false;

        var delete = new Button
        {
            Content = "\u6C38\u4E45\u5220\u9664",
            MinWidth = 112,
            Height = 40,
            Background = new SolidColorBrush(Color.FromRgb(255, 106, 106)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 149, 149)),
            Foreground = new SolidColorBrush(Color.FromRgb(24, 6, 9)),
            Style = DangerButtonStyle()
        };
        delete.Click += (_, _) => DialogResult = true;

        footer.Children.Add(cancel);
        footer.Children.Add(delete);
        root.Children.Add(footer);

        shell.Child = root;
        Content = shell;
        Loaded += (_, _) => cancel.Focus();
    }
    private static Style DangerButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(24, 6, 9))));
        style.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 106, 106))));
        style.Setters.Add(new Setter(BorderBrushProperty, new SolidColorBrush(Color.FromRgb(255, 149, 149))));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(14, 8, 14, 8)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(PaddingProperty));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 132, 132)), "ButtonBorder"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(255, 186, 186)), "ButtonBorder"));
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(234, 82, 82)), "ButtonBorder"));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45));
        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }
}
