using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private GalleryEntry? _inspectedItem;
    private BitmapSource? _inspectedThumbnail;
    private string? _inspectorAiDraft = null;
    private bool _inspectorVisible;

    private void OpenInspector(GalleryCardViewModel card)
    {
        OpenInspector(card.Item, card.Thumbnail);
    }

    private void OpenInspector(GalleryEntry item, BitmapSource? thumbnail)
    {
        _inspectedItem = item;
        _inspectedThumbnail = thumbnail;
        PopulateInspector(item, thumbnail);
        SetInspectorVisibility(true);
    }

    private void PopulateInspector(GalleryEntry item, BitmapSource? thumbnail)
    {
        InspectorPreview.Source = thumbnail;
        InspectorTitle.Text = Path.GetFileName(item.OriginalPath);
        InspectorMetaText.Text = string.Join(
            "  ·  ",
            new[]
            {
                string.IsNullOrWhiteSpace(item.CategoryName) ? "未分类" : item.CategoryName,
                item.IsFavorite ? "已收藏" : null
            }.Where(value => value is not null));
        InspectorOriginalInfoText.Text =
            $"{Math.Max(0, item.Width):N0} × {Math.Max(0, item.Height):N0} px  ·  {item.Format.ToUpperInvariant()}  ·  {item.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
        InspectorPromptEditor.Text = item.Prompt;
        InspectorTagsEditor.Text = item.Tags;
        InspectorNotesEditor.Text = item.Notes;
        InspectorSourceText.Text = item.IsExternal
            ? $"外部文件夹\n{item.OriginalPath}"
            : $"图库原图\n{ResolveOriginalPath(item)}";
        InspectorAiDraftText.Text = string.IsNullOrWhiteSpace(_inspectorAiDraft)
            ? "尚无 AI 草稿 · M4 分析后显示"
            : _inspectorAiDraft;
        InspectorAestheticText.Text = "尚无审美属性 · M4 分析后显示";

        var editable = !item.IsExternal && item.DeletedAt is null;
        InspectorPromptEditor.IsReadOnly = !editable;
        InspectorTagsEditor.IsReadOnly = !editable;
        InspectorNotesEditor.IsReadOnly = !editable;
        InspectorSaveButton.IsEnabled = editable;
        InspectorConfirmAiButton.IsEnabled = editable && !string.IsNullOrWhiteSpace(_inspectorAiDraft);
        UpdateInspectorPinVisual();
    }

    private void RestoreInspectorAfterRefresh()
    {
        if (_inspectedItem is null) return;
        var current = _items.FirstOrDefault(item => item.Id == _inspectedItem.Id);
        if (current is not null)
        {
            _inspectedItem = current;
            var card = Rows.SelectMany(row => row.Items).FirstOrDefault(candidate => candidate.Id == current.Id);
            if (card?.Thumbnail is not null) _inspectedThumbnail = card.Thumbnail;
            PopulateInspector(current, _inspectedThumbnail);
            return;
        }

        if (!_settings.InspectorPinned)
        {
            SetInspectorVisibility(false);
        }
    }

    private void SetInspectorVisibility(bool visible)
    {
        if (visible)
        {
            _inspectorVisible = true;
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorPanel.IsHitTestVisible = true;
            AnimateInspector(0, 1, collapseWhenComplete: false);
            return;
        }

        if (!_inspectorVisible) return;
        _inspectorVisible = false;
        InspectorPanel.IsHitTestVisible = false;
        AnimateInspector(408, 0, collapseWhenComplete: true);
    }

    private void AnimateInspector(double translateTo, double opacityTo, bool collapseWhenComplete)
    {
        var duration = VisualModeService.Motion(MotionToken.Panel);
        if (duration == TimeSpan.Zero)
        {
            InspectorTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            InspectorPanel.BeginAnimation(OpacityProperty, null);
            InspectorTransform.X = translateTo;
            InspectorPanel.Opacity = opacityTo;
            if (collapseWhenComplete) InspectorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        InspectorTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(translateTo, duration) { EasingFunction = ease });
        var opacity = new DoubleAnimation(opacityTo, duration) { EasingFunction = ease };
        if (collapseWhenComplete)
        {
            opacity.Completed += (_, _) =>
            {
                if (!_inspectorVisible) InspectorPanel.Visibility = Visibility.Collapsed;
            };
        }
        InspectorPanel.BeginAnimation(OpacityProperty, opacity);
    }

    private void InspectorPinClick(object sender, RoutedEventArgs e)
    {
        _settings.InspectorPinned = !_settings.InspectorPinned;
        _settings.Save();
        UpdateInspectorPinVisual();
        ToastService.Show(this, _settings.InspectorPinned ? "检查器已固定" : "检查器已取消固定");
    }

    private void UpdateInspectorPinVisual()
    {
        if (InspectorPinButton is null) return;
        InspectorPinButton.Content = _settings.InspectorPinned ? "已固定" : "固定";
        InspectorPinButton.BorderBrush = _settings.InspectorPinned
            ? VisualModeService.ResourceBrush("AccentBrush")
            : VisualModeService.ResourceBrush("ButtonBorderBrush");
    }

    private void CloseInspectorClick(object sender, RoutedEventArgs e)
    {
        if (_settings.InspectorPinned)
        {
            _settings.InspectorPinned = false;
            _settings.Save();
        }
        SetInspectorVisibility(false);
    }

    private async void SaveInspectorClick(object sender, RoutedEventArgs e)
    {
        if (_inspectedItem is not { IsExternal: false, DeletedAt: null } item) return;
        InspectorSaveButton.IsEnabled = false;
        try
        {
            var prompt = InspectorPromptEditor.Text.Trim();
            var tags = InspectorTagsEditor.Text.Trim();
            var notes = InspectorNotesEditor.Text.Trim();
            await _repository.UpdateItemDetailsAsync(item.Id, prompt, tags, notes);
            _inspectedItem = item with { Prompt = prompt, Tags = tags, Notes = notes };
            ToastService.Show(this, "检查器修改已保存");
            await RefreshAsync(RefreshAnimationKind.ContentChange);
            RestoreInspectorAfterRefresh();
        }
        catch (Exception ex)
        {
            AppLog.Warning("inspector-save", "Inspector quick edit could not be saved.", ex);
            ToastService.Show(this, $"保存失败：{ex.Message}");
        }
        finally
        {
            if (_inspectedItem is { IsExternal: false, DeletedAt: null })
            {
                InspectorSaveButton.IsEnabled = true;
            }
        }
    }

    private void ConfirmInspectorAiClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_inspectorAiDraft))
        {
            ToastService.Show(this, "尚无可确认的 AI 草稿");
            return;
        }

        InspectorPromptEditor.Text = _inspectorAiDraft;
        ToastService.Show(this, "AI 草稿已填入提示词，保存后生效");
    }

    private void AddInspectorToBoardClick(object sender, RoutedEventArgs e)
    {
        if (_inspectedItem is null) return;
        ToastService.Show(this, "画板入口已预留，M5 将启用持久画板");
    }

    private void OpenInspectorSourceClick(object sender, RoutedEventArgs e)
    {
        if (_inspectedItem is null) return;
        var path = ResolveOriginalPath(_inspectedItem);
        if (!File.Exists(path))
        {
            ToastService.Show(this, "原图文件当前不可用");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warning("inspector-open-source", "Inspector source file could not be opened.", ex);
            ToastService.Show(this, $"无法打开原图：{ex.Message}");
        }
    }
}
