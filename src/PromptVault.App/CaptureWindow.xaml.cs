using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class CaptureWindow : Window
{
    private PendingCapture _pending = null!;
    private int _pendingVersion;
    private readonly IReadOnlyList<CategoryRecord> _categories;
    private bool _closingInternally;
    public event Func<string, string, long?, string, Task>? SaveRequested;
    public event Action? CancelRequested;

    public CaptureWindow(PendingCapture pending, IReadOnlyList<CategoryRecord> categories)
    {
        _pending = pending;
        _categories = categories;
        InitializeComponent();
        CategoryBox.ItemsSource = new[] { new CaptureCategoryChoice(null, "未分类") }
            .Concat(categories.Select(x => new CaptureCategoryChoice(x.Id, x.Name))).ToArray();
        ApplyPendingState(pending);
        Loaded += async (_, _) => { PositionAtRight(); await ApplyAiSuggestionAsync(); };
    }

    private void ApplyPendingState(PendingCapture pending)
    {
        _pending = pending;
        _pendingVersion++;
        PreviewImage.Source = pending.Preview;
        PromptBox.Clear();
        NotesBox.Clear();
        TagsBox.Clear();
        ErrorText.Text = "";
        SaveButton.IsEnabled = true;
        CategoryBox.SelectedIndex = 0;
        StateText.Text = "等待你复制提示词…";
        if (pending.ExistingItem is not { } existing) return;
        StateText.Text = "发现相同图片，将更新原记录";
        PromptBox.Text = existing.Prompt;
        NotesBox.Text = existing.Notes;
        TagsBox.Text = existing.Tags;
        CategoryBox.SelectedValue = existing.CategoryId;
    }

    public async Task ReplacePendingAsync(PendingCapture pending)
    {
        ApplyPendingState(pending);
        await ApplyAiSuggestionAsync();
    }
    public void SetPrompt(string prompt)
    {
        PromptBox.Text = prompt.Trim();
        StateText.Text = "已捕获提示词，请确认分类后保存";
    }

    public void ShowError(string message) { ErrorText.Text = message; SaveButton.IsEnabled = true; }
    public void CloseAfterSave() { _closingInternally = true; Close(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingInternally) CancelRequested?.Invoke();
        base.OnClosing(e);
    }

    private async Task ApplyAiSuggestionAsync()
    {
        var version = _pendingVersion;
        if (_pending.ExistingItem is not null) return;
        var root = Directory.GetParent(Path.GetDirectoryName(_pending.StagedOriginal)!)?.FullName;
        if (root is null) return;
        var suggestion = await new LocalAiClassifier(Path.Combine(root, "models")).SuggestAsync(_pending.StagedOriginal, _categories);
        if (version != _pendingVersion) return;
        if (suggestion.Categories.FirstOrDefault() is { } category)
        {
            var match = _categories.FirstOrDefault(x => x.Name.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (match is not null) CategoryBox.SelectedValue = match.Id;
        }
        if (suggestion.Tags.Count > 0) TagsBox.Text = string.Join(", ", suggestion.Tags);
        if (suggestion.UsedModel) StateText.Text = "本地 AI 已给出分类建议，等待提示词…";
    }

    private async void SaveClick(object sender, RoutedEventArgs e) => await RequestSaveAsync();
    private async Task RequestSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) { ShowError("请先复制或输入提示词。"); return; }
        SaveButton.IsEnabled = false; ErrorText.Text = "";
        long? category = CategoryBox.SelectedValue is long id ? id : null;
        if (SaveRequested is { } handler) await handler(PromptBox.Text, NotesBox.Text, category, TagsBox.Text);
    }

    private void CancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void DeletePendingClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelRequested?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { await RequestSaveAsync(); e.Handled = true; }
    }


    private void CaptureDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveDragSource(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private static bool IsInteractiveDragSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or Selector or System.Windows.Controls.Primitives.ScrollBar) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
    private sealed record CaptureCategoryChoice(long? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private void PositionAtRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 20;
        Top = area.Top + Math.Max(20, (area.Height - ActualHeight) / 2);
    }
}
