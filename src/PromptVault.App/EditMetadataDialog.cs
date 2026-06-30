using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PromptVault.App;

public sealed class EditMetadataDialog : Window
{
    private readonly System.Windows.Controls.CheckBox _applyTags = new();
    private readonly System.Windows.Controls.CheckBox _applyNotes = new();
    private readonly TextBox _tags = new();
    private readonly TextBox _notes = new();
    private readonly Button _save = new();

    public bool ApplyTags => _applyTags.IsChecked == true;
    public bool ApplyNotes => _applyNotes.IsChecked == true;
    public string Tags => _tags.Text.Trim();
    public string Notes => _notes.Text.Trim();

    public EditMetadataDialog(int itemCount, string initialTags, string initialNotes, bool isBatch)
    {
        Title = isBatch ? "\u6279\u91CF\u4FEE\u6539\u6807\u7B7E\u548C\u5907\u6CE8" : "\u4FEE\u6539\u6807\u7B7E\u548C\u5907\u6CE8";
        Width = 520;
        Height = 430;
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
            Text = isBatch ? $"\u6279\u91CF\u4FEE\u6539 {itemCount} \u5F20" : "\u4FEE\u6539\u6807\u7B7E\u548C\u5907\u6CE8",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255)),
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button { Content = "\u00D7", Width = 40, Height = 34, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(27, 42, 59)) };
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var form = new StackPanel();
        Grid.SetRow(form, 1);
        _applyTags.Content = "\u66F4\u65B0\u6807\u7B7E";
        _applyTags.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        _applyTags.Margin = new Thickness(0, 0, 0, 7);
        _applyTags.IsChecked = !isBatch;
        form.Children.Add(_applyTags);

        _tags.Text = initialTags;
        _tags.Height = 48;
        _tags.FontSize = 15;
        _tags.VerticalContentAlignment = VerticalAlignment.Center;
        _tags.Margin = new Thickness(0, 0, 0, 16);
        _tags.ToolTip = "\u591A\u4E2A\u6807\u7B7E\u53EF\u7528\u9017\u53F7\u6216\u5206\u53F7\u5206\u9694";
        form.Children.Add(_tags);

        _applyNotes.Content = "\u66F4\u65B0\u5907\u6CE8";
        _applyNotes.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        _applyNotes.Margin = new Thickness(0, 0, 0, 7);
        _applyNotes.IsChecked = !isBatch;
        form.Children.Add(_applyNotes);

        _notes.Text = initialNotes;
        _notes.Height = 116;
        _notes.FontSize = 15;
        _notes.TextWrapping = TextWrapping.Wrap;
        _notes.AcceptsReturn = true;
        _notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        form.Children.Add(_notes);

        if (isBatch)
        {
            form.Children.Add(new TextBlock
            {
                Text = "\u6279\u91CF\u6A21\u5F0F\u53EA\u4F1A\u5199\u5165\u5DF2\u52FE\u9009\u7684\u5B57\u6BB5\u3002",
                Foreground = new SolidColorBrush(Color.FromRgb(130, 146, 166)),
                Margin = new Thickness(0, 10, 0, 0)
            });
        }
        root.Children.Add(form);

        var footer = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        Grid.SetRow(footer, 2);
        var cancel = new Button { Content = "\u53D6\u6D88", MinWidth = 88, Height = 40, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        _save.Content = "\u4FDD\u5B58";
        _save.MinWidth = 92;
        _save.Height = 40;
        _save.IsDefault = true;
        _save.Background = new SolidColorBrush(Color.FromRgb(86, 214, 255));
        _save.Foreground = new SolidColorBrush(Color.FromRgb(5, 15, 24));
        _save.Click += (_, _) => { if (ApplyTags || ApplyNotes) DialogResult = true; };
        footer.Children.Add(cancel);
        footer.Children.Add(_save);
        root.Children.Add(footer);

        _applyTags.Checked += (_, _) => UpdateEnabledState();
        _applyTags.Unchecked += (_, _) => UpdateEnabledState();
        _applyNotes.Checked += (_, _) => UpdateEnabledState();
        _applyNotes.Unchecked += (_, _) => UpdateEnabledState();

        shell.Child = root;
        Content = shell;
        Loaded += (_, _) => { UpdateEnabledState(); if (ApplyTags) _tags.Focus(); else _notes.Focus(); };
    }

    private void UpdateEnabledState()
    {
        _tags.IsEnabled = ApplyTags;
        _notes.IsEnabled = ApplyNotes;
        _save.IsEnabled = ApplyTags || ApplyNotes;
        _tags.Opacity = ApplyTags ? 1 : 0.45;
        _notes.Opacity = ApplyNotes ? 1 : 0.45;
    }
}