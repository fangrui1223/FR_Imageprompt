using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class CaptureWindow : Window
{
    private PendingCapture _pending = null!;
    private int _pendingVersion;
    private readonly IReadOnlyList<CategoryRecord> _categories;
    private readonly bool _activateForEditing;
    private bool _closingInternally;
    private bool _retainPendingOnClose;
    private bool _applyingState;
    private bool _manualCategory;
    private bool _manualTags;
    private bool _manualNotes;
    private bool _otherAiCategory;
    private readonly DispatcherTimer _draftChangedTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };
    public event Func<string, string, long?, string, Task>? SaveRequested;
    public event Func<string, string, long?, string, Task>? DraftChanged;
    public event Action? CloseRequested;
    public event Action? DeleteRequested;

    public CaptureWindow(
        PendingCapture pending,
        IReadOnlyList<CategoryRecord> categories,
        bool activateForEditing = false)
    {
        _pending = pending;
        _categories = categories;
        _activateForEditing = activateForEditing;
        InitializeComponent();
        _draftChangedTimer.Tick += async (_, _) =>
        {
            _draftChangedTimer.Stop();
            await EmitDraftChangedAsync();
        };
        CategoryBox.ItemsSource = new[] { new CaptureCategoryChoice(null, "未分类") }
            .Concat(categories.Select(x => new CaptureCategoryChoice(x.Id, x.Name))).ToArray();
        ApplyPendingState(pending);
        Loaded += async (_, _) =>
        {
            PositionAtRight();
            await ApplyAiSuggestionAsync();
            if (_activateForEditing)
            {
                await Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    ActivateForEditing);
            }
        };
    }

    private void ApplyPendingState(PendingCapture pending)
    {
        _applyingState = true;
        _pending = pending;
        _pendingVersion++;
        PreviewImage.Source = pending.Preview;
        PromptBox.Clear();
        NotesBox.Clear();
        TagsBox.Clear();
        ErrorText.Text = "";
        SaveButton.IsEnabled = true;
        CategoryBox.SelectedIndex = 0;
        _manualCategory = false;
        _manualTags = false;
        _manualNotes = false;
        _otherAiCategory = false;
        StateText.Text = "等待你复制提示词…";
        if (pending.ExistingItem is { } existing)
        {
            StateText.Text = "发现相同图片，将更新原记录";
            PromptBox.Text = existing.Prompt;
            NotesBox.Text = existing.Notes;
            TagsBox.Text = existing.Tags;
            CategoryBox.SelectedValue = existing.CategoryId;
        }
        _applyingState = false;
    }

    public async Task ReplacePendingAsync(PendingCapture pending)
    {
        ApplyPendingState(pending);
        await ApplyAiSuggestionAsync();
    }
    public void SetPrompt(string prompt)
    {
        PromptBox.Text = prompt.Trim();
        StateText.Text = "已捕获提示词，文本稳定后会自动保存";
    }

    public void SetDraft(CaptureSessionRecord session)
    {
        _applyingState = true;
        PromptBox.Text = session.Prompt;
        NotesBox.Text = session.Notes;
        TagsBox.Text = session.Tags;
        CategoryBox.SelectedValue = session.CategoryId;
        if (session.State == CaptureState.NeedsPrompt)
        {
            StateText.Text = "这张图片正在待补提示词收件箱中";
        }
        else if (session.State == CaptureState.Failed)
        {
            StateText.Text = "上次收录失败，内容已保留";
            ErrorText.Text = session.Error ?? "";
        }
        _applyingState = false;
    }

    public void ShowError(string message) { ErrorText.Text = message; SaveButton.IsEnabled = true; }
    public void CloseAfterSave() { _closingInternally = true; Close(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _draftChangedTimer.Stop();
        if (!_closingInternally)
        {
            if (_retainPendingOnClose || System.Windows.Application.Current.Dispatcher.HasShutdownStarted)
            {
                CloseRequested?.Invoke();
            }
            else
            {
                DeleteRequested?.Invoke();
            }
        }
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
            if (match is not null && !_manualCategory && !_otherAiCategory)
            {
                _applyingState = true;
                CategoryBox.SelectedValue = match.Id;
                _applyingState = false;
            }
        }
        if (suggestion.Tags.Count > 0 && !_manualTags)
        {
            _applyingState = true;
            TagsBox.Text = string.Join(", ", suggestion.Tags);
            _applyingState = false;
        }
        if (suggestion.UsedModel && !_manualNotes)
        {
            StateText.Text = "本地 AI 已给出分类建议，等待提示词…";
        }
    }

    private async void SaveClick(object sender, RoutedEventArgs e) => await RequestSaveAsync();
    private async Task RequestSaveAsync()
    {
        var prompt = PromptBox.Text.Trim();
        long? category = CategoryBox.SelectedValue is long id ? id : null;
        _draftChangedTimer.Stop();
        await EmitDraftChangedAsync();
        if (string.IsNullOrWhiteSpace(prompt) && category is null)
        {
            StateText.Text = "已保留到待补提示词收件箱";
            _retainPendingOnClose = true;
            Close();
            return;
        }
        SaveButton.IsEnabled = false; ErrorText.Text = "";
        if (SaveRequested is { } handler) await handler(prompt, NotesBox.Text, category, TagsBox.Text);
    }

    private void CancelClick(object sender, RoutedEventArgs e) => CancelCapture();
    private void DeletePendingClick(object sender, RoutedEventArgs e) => CancelCapture();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        var shortcutKey = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };
        if (shortcutKey == Key.Escape) { CancelCapture(); e.Handled = true; }
        else if (shortcutKey == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            await RequestSaveAsync();
            e.Handled = true;
        }
        else if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase
                 and not PasswordBox)
        {
            if (shortcutKey is Key.D1 or Key.NumPad1)
            {
                SelectManualCategory("上衣参考");
                e.Handled = true;
            }
            else if (shortcutKey is Key.D2 or Key.NumPad2)
            {
                SelectManualCategory("裤子参考");
                e.Handled = true;
            }
            else if (shortcutKey is Key.D0 or Key.NumPad0)
            {
                OtherAiClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    public void ActivateForEditing()
    {
        ShowActivated = true;
        Activate();
        CategoryBox.Focus();
        Keyboard.Focus(CategoryBox);
    }

    private void TopReferenceClick(object sender, RoutedEventArgs e) =>
        SelectManualCategory("上衣参考");

    private void PantsReferenceClick(object sender, RoutedEventArgs e) =>
        SelectManualCategory("裤子参考");

    private void OtherAiClick(object sender, RoutedEventArgs e)
    {
        _manualCategory = false;
        _otherAiCategory = true;
        _applyingState = true;
        CategoryBox.SelectedValue = null;
        CategoryBox.SelectedIndex = 0;
        _applyingState = false;
        StateText.Text = "主分类交给 AI 候选；保存前不会自动确认";
        ScheduleDraftChanged();
    }

    private void SelectManualCategory(string name)
    {
        var category = _categories.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
        if (category is null)
        {
            ShowError($"分类“{name}”暂不可用，请重新打开快速标注。");
            return;
        }
        _manualCategory = true;
        _otherAiCategory = false;
        _applyingState = true;
        CategoryBox.SelectedValue = category.Id;
        _applyingState = false;
        StateText.Text = $"已选择人工主分类：{name}";
        ScheduleDraftChanged();
    }

    private void CategorySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_applyingState) return;
        _manualCategory = CategoryBox.SelectedValue is long;
        _otherAiCategory = !_manualCategory;
        ScheduleDraftChanged();
    }

    private void TagsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingState) return;
        _manualTags = true;
        ScheduleDraftChanged();
    }

    private void NotesTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingState) return;
        _manualNotes = true;
        ScheduleDraftChanged();
    }

    private void PromptTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_applyingState) ScheduleDraftChanged();
    }

    private void ScheduleDraftChanged()
    {
        _draftChangedTimer.Stop();
        _draftChangedTimer.Start();
    }

    private async Task EmitDraftChangedAsync()
    {
        if (DraftChanged is not { } handler) return;
        long? category = CategoryBox.SelectedValue is long id ? id : null;
        await handler(PromptBox.Text, NotesBox.Text, category, TagsBox.Text);
    }

    private void CancelCapture()
    {
        DeleteRequested?.Invoke();
        _closingInternally = true;
        Close();
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
