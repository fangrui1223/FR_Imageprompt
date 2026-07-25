using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PromptVault.Core;

namespace PromptVault.App;

public sealed record CaptureInboxSaveRequest(Guid CaptureId, string Prompt);

public sealed class CaptureInboxItemViewModel : INotifyPropertyChanged
{
    private string _prompt;
    private bool _isSelected;
    private string _error;

    public CaptureInboxItemViewModel(
        CaptureSessionRecord session,
        BitmapSource? preview,
        bool canProcess)
    {
        CaptureId = session.Id;
        Preview = preview;
        StateText = session.State == CaptureState.Failed ? "需要处理" : "等待提示词";
        DetailText = $"{session.Width} × {session.Height}  ·  {session.CapturedAt.LocalDateTime:g}";
        _prompt = session.Prompt;
        _error = session.Error ?? (canProcess ? "" : "暂存图片不可用，请删除后重新收录。");
        CanProcess = canProcess;
    }

    public Guid CaptureId { get; }
    public BitmapSource? Preview { get; }
    public string StateText { get; }
    public string DetailText { get; }
    public bool CanProcess { get; }
    public string Prompt
    {
        get => _prompt;
        set
        {
            if (_prompt == value) return;
            _prompt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSave));
        }
    }
    public string Error
    {
        get => _error;
        set
        {
            if (_error == value) return;
            _error = value;
            OnPropertyChanged();
        }
    }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }
    public bool CanSave => CanProcess && !string.IsNullOrWhiteSpace(Prompt);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class CaptureInboxWindow : Window
{
    private bool _isBusy;

    public CaptureInboxWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        DataContext = this;
        UpdateSummary();
    }

    public ObservableCollection<CaptureInboxItemViewModel> Items { get; } = [];

    public event Func<IReadOnlyList<CaptureInboxSaveRequest>, Task>? SaveRequested;
    public event Func<IReadOnlyList<Guid>, Task>? DeleteRequested;
    public event Func<Task>? RefreshRequested;

    public void ReplaceItems(IEnumerable<CaptureInboxItemViewModel> items)
    {
        var drafts = Items.ToDictionary(item => item.CaptureId, item => item.Prompt);
        Items.Clear();
        foreach (var item in items)
        {
            if (drafts.TryGetValue(item.CaptureId, out var draft)
                && !string.IsNullOrWhiteSpace(draft))
            {
                item.Prompt = draft;
            }
            Items.Add(item);
        }
        UpdateSummary();
    }

    public void SetItemError(Guid captureId, string error)
    {
        var item = Items.FirstOrDefault(candidate => candidate.CaptureId == captureId);
        if (item is not null) item.Error = error;
    }

    private async void SaveItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CaptureInboxItemViewModel item) return;
        await SaveAsync([item]);
    }

    private async void DeleteItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CaptureInboxItemViewModel item) return;
        if (!ConfirmDelete(1)) return;
        await DeleteAsync([item]);
    }

    private async void SaveSelectedClick(object sender, RoutedEventArgs e) =>
        await SaveAsync(Items.Where(item => item.IsSelected).ToArray());

    private async void DeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = Items.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0 || !ConfirmDelete(selected.Length)) return;
        await DeleteAsync(selected);
    }

    private void ToggleAllClick(object sender, RoutedEventArgs e)
    {
        var select = Items.Any(item => !item.IsSelected);
        foreach (var item in Items) item.IsSelected = select;
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || RefreshRequested is null) return;
        await RunBusyAsync(() => RefreshRequested());
    }

    private async Task SaveAsync(IReadOnlyCollection<CaptureInboxItemViewModel> items)
    {
        if (_isBusy || SaveRequested is null) return;
        var requests = items
            .Where(item => item.CanSave)
            .Select(item => new CaptureInboxSaveRequest(item.CaptureId, item.Prompt.Trim()))
            .ToArray();
        if (requests.Length == 0)
        {
            SummaryText.Text = "请先选择项目并填写提示词。";
            return;
        }
        await RunBusyAsync(() => SaveRequested(requests));
    }

    private async Task DeleteAsync(IReadOnlyCollection<CaptureInboxItemViewModel> items)
    {
        if (_isBusy || DeleteRequested is null || items.Count == 0) return;
        await RunBusyAsync(() => DeleteRequested(items.Select(item => item.CaptureId).ToArray()));
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _isBusy = true;
        IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            IsEnabled = true;
            _isBusy = false;
            UpdateSummary();
        }
    }

    private bool ConfirmDelete(int count) =>
        MessageBox.Show(
            this,
            $"确定删除 {count} 个待补项目吗？暂存图片也会被移除，此操作无法撤销。",
            "删除待补项目",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private void UpdateSummary()
    {
        SummaryText.Text = Items.Count == 0
            ? "图片会安全保留在这里，直到补充提示词或手动删除。"
            : $"{Items.Count} 个项目等待处理；关闭窗口不会删除任何内容。";
        EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxList.Visibility = Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
