using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PromptVault.Core;

namespace PromptVault.App;

public partial class AiReviewWindow : Window
{
    private readonly LibraryRepository _repository;
    private readonly long? _preferredItemId;
    private IReadOnlyList<MetadataCandidateRecord> _items = [];
    private int _index;
    private bool _busy;

    public AiReviewWindow(Window owner, LibraryRepository repository, long? preferredItemId = null)
    {
        InitializeComponent();
        Owner = owner;
        _repository = repository;
        _preferredItemId = preferredItemId;
        Loaded += async (_, _) => await ReloadAsync();
    }

    public event Func<Task>? MetadataChanged;

    private async Task ReloadAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var items = await _repository.GetPendingMetadataCandidatesAsync(1000);
            _items = items
                .OrderByDescending(item => item.ItemId == _preferredItemId)
                .ThenBy(item => item.UpdatedAt)
                .ThenBy(item => item.Id)
                .ToArray();
            _index = Math.Clamp(_index, 0, Math.Max(0, _items.Count - 1));
            await ShowCurrentAsync();
        }
        finally { _busy = false; }
    }

    private async Task ShowCurrentAsync()
    {
        var hasItem = _items.Count > 0;
        EmptyText.Visibility = hasItem ? Visibility.Collapsed : Visibility.Visible;
        if (!hasItem)
        {
            PreviewImage.Source = null;
            PositionText.Text = "";
            FieldText.Text = "";
            ProviderText.Text = "";
            ValueEditor.Text = "";
            ConfidenceText.Text = "";
            SummaryText.Text = "所有 AI 草稿均已处理";
            return;
        }
        var candidate = _items[_index];
        PositionText.Text = $"{_index + 1} / {_items.Count}";
        SummaryText.Text = $"{_items.Select(item => item.ItemId).Distinct().Count()} 张图片 · {_items.Count} 个候选";
        FieldText.Text = FieldLabel(candidate.FieldType);
        ProviderText.Text = $"{candidate.ModelName} · {candidate.ModelVersion[..Math.Min(10, candidate.ModelVersion.Length)]} · {SourceLabel(candidate.Source)}";
        ValueEditor.Text = candidate.Value;
        ConfidenceText.Text = candidate.Confidence is { } confidence
            ? $"模型置信度：{confidence:P1}"
            : "模型未提供可比较置信度";
        var item = await _repository.GetGalleryItemAsync(candidate.ItemId);
        PreviewImage.Source = item is null ? null : LoadPreview(_repository.Paths.ToAbsolute(item.MediumThumbnailPath));
        ValueEditor.Focus();
        ValueEditor.SelectAll();
    }

    private async Task ConfirmAsync()
    {
        if (_busy || _items.Count == 0) return;
        _busy = true;
        try
        {
            await _repository.ConfirmMetadataCandidateAsync(_items[_index].Id, ValueEditor.Text);
            if (MetadataChanged is { } changed) await changed();
            _items = _items.Where((_, index) => index != _index).ToArray();
            _index = Math.Clamp(_index, 0, Math.Max(0, _items.Count - 1));
            await ShowCurrentAsync();
        }
        finally { _busy = false; }
    }

    private async Task RejectAsync()
    {
        if (_busy || _items.Count == 0) return;
        _busy = true;
        try
        {
            await _repository.RejectMetadataCandidateAsync(_items[_index].Id);
            if (MetadataChanged is { } changed) await changed();
            _items = _items.Where((_, index) => index != _index).ToArray();
            _index = Math.Clamp(_index, 0, Math.Max(0, _items.Count - 1));
            await ShowCurrentAsync();
        }
        finally { _busy = false; }
    }

    private async void ConfirmClick(object sender, RoutedEventArgs e) => await ConfirmAsync();
    private async void RejectClick(object sender, RoutedEventArgs e) => await RejectAsync();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void PreviousClick(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        _index = (_index - 1 + _items.Count) % _items.Count;
        await ShowCurrentAsync();
    }
    private async void NextClick(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        _index = (_index + 1) % _items.Count;
        await ShowCurrentAsync();
    }
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            await ConfirmAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.R && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            await RejectAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) PreviousClick(sender, e);
        else if (e.Key == Key.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) NextClick(sender, e);
    }

    private static BitmapSource? LoadPreview(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 1200;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string FieldLabel(string field) => field switch
    {
        "description" => "中文描述",
        "category" => "分类",
        "tags" => "标签",
        "style" => "风格",
        "lighting" => "光影",
        "color" => "色彩",
        "composition" => "构图",
        "texture" => "质感",
        "atmosphere" => "氛围",
        _ => field
    };

    private static string SourceLabel(AiMetadataSource source) => source switch
    {
        AiMetadataSource.LocalModel => "本地模型",
        AiMetadataSource.OnlineApi => "在线 API",
        AiMetadataSource.Rule => "规则",
        _ => "用户"
    };
}
