using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class MainWindow
{
    private GalleryEntry? _inspectedItem;
    private BitmapSource? _inspectedThumbnail;
    private string? _inspectorAiDraft = null;
    private MetadataCandidateRecord? _inspectorAiCandidate;
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
        _inspectorAiDraft = null;
        _inspectorAiCandidate = null;
        InspectorAiDraftText.Text = "正在读取 AI 元数据…";
        InspectorAiDraftText.IsReadOnly = true;
        InspectorAiSourceText.Text = "";
        InspectorAestheticText.Text = "正在读取审美属性…";

        var editable = !item.IsExternal && item.DeletedAt is null;
        InspectorPromptEditor.IsReadOnly = !editable;
        InspectorTagsEditor.IsReadOnly = !editable;
        InspectorNotesEditor.IsReadOnly = !editable;
        InspectorSaveButton.IsEnabled = editable;
        InspectorOnlineAiButton.IsEnabled = editable;
        InspectorConfirmAiButton.IsEnabled = editable && !string.IsNullOrWhiteSpace(_inspectorAiDraft);
        InspectorRejectAiButton.IsEnabled = false;
        UpdateInspectorPinVisual();
        _ = LoadInspectorAiAsync(item.Id, editable);
    }

    private async Task LoadInspectorAiAsync(long itemId, bool editable)
    {
        try
        {
            var candidates = await _repository.GetMetadataCandidatesAsync(itemId);
            var authoritative = await _repository.GetUserMetadataAsync(itemId, "description");
            if (_inspectedItem?.Id != itemId) return;
            _inspectorAiCandidate = candidates.FirstOrDefault(candidate =>
                candidate.FieldType == "description"
                && candidate.Status == MetadataCandidateStatus.Pending);
            _inspectorAiDraft = _inspectorAiCandidate?.Value;
            InspectorAiDraftText.Text = authoritative?.Value
                ?? _inspectorAiDraft
                ?? "尚无 AI 草稿，可通过待校正入口加入分析队列";
            InspectorAiDraftText.IsReadOnly = authoritative is not null || _inspectorAiCandidate is null;
            InspectorAiSourceText.Text = authoritative is not null
                ? "用户已确认 · 权威元数据"
                : _inspectorAiCandidate is { } draft
                    ? $"{draft.ModelName} · {draft.Source} · {(draft.Confidence is { } score ? score.ToString("P1") : "无置信度")}"
                    : "";
            var aesthetics = candidates
                .Where(candidate => candidate.FieldType is
                    "style" or "lighting" or "color" or "composition" or "texture" or "atmosphere")
                .Select(candidate => $"{candidate.FieldType}：{candidate.Value}")
                .ToArray();
            InspectorAestheticText.Text = aesthetics.Length == 0
                ? "尚无审美属性"
                : string.Join("  ·  ", aesthetics);
            InspectorConfirmAiButton.IsEnabled = editable && _inspectorAiCandidate is not null;
            InspectorRejectAiButton.IsEnabled = editable && _inspectorAiCandidate is not null;
        }
        catch (Exception ex)
        {
            AppLog.Warning("inspector-ai", "Inspector AI metadata could not be loaded.", ex);
            if (_inspectedItem?.Id == itemId) InspectorAiDraftText.Text = "AI 元数据暂时不可用";
        }
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

    private async void ConfirmInspectorAiClick(object sender, RoutedEventArgs e)
    {
        if (_inspectorAiCandidate is not { } candidate)
        {
            ToastService.Show(this, "尚无可确认的 AI 草稿");
            return;
        }
        await _repository.ConfirmMetadataCandidateAsync(candidate.Id, InspectorAiDraftText.Text);
        ToastService.Show(this, "AI 草稿已确认为用户元数据");
        if (_inspectedItem is { } item) await LoadInspectorAiAsync(item.Id, !item.IsExternal && item.DeletedAt is null);
    }

    private async void RejectInspectorAiClick(object sender, RoutedEventArgs e)
    {
        if (_inspectorAiCandidate is not { } candidate) return;
        await _repository.RejectMetadataCandidateAsync(candidate.Id);
        ToastService.Show(this, "AI 草稿已拒绝，模型重跑不会恢复本版本结果");
        if (_inspectedItem is { } item) await LoadInspectorAiAsync(item.Id, !item.IsExternal && item.DeletedAt is null);
    }

    private async void OpenAiReviewClick(object sender, RoutedEventArgs e)
    {
        await _clipboard.ShowAiReviewAsync(_inspectedItem?.Id);
    }

    private async void AnalyzeInspectorOnlineClick(object sender, RoutedEventArgs e)
    {
        if (_inspectedItem is not { IsExternal: false, DeletedAt: null } item) return;
        if (!_settings.OnlineAiEnabled
            || !OnlineAiConfiguration.TryCreate(_settings, out _, out _)
            || !WindowsCredentialStore.HasOnlineAiKey())
        {
            OpenAiSettingsClick(sender, e);
            ToastService.Show(this, "完成在线 AI 设置后，请再次点击“在线分析”");
            return;
        }

        InspectorOnlineAiButton.IsEnabled = false;
        try
        {
            ToastService.Show(this, "在线分析已交给独立 Worker");
            if (!await _aiWorker.EnqueueOnlineAndRunAsync(item.Id, item.Hash))
            {
                ToastService.Show(this, "在线调用未完成；本地图库和收录未受影响");
                return;
            }
            await LoadInspectorAiAsync(item.Id, editable: true);
            ToastService.Show(this, "在线草稿已生成，请确认或修改");
        }
        catch (Exception ex)
        {
            AppLog.Warning("online-ai", "Online AI analysis could not complete.", ex);
            ToastService.Show(this, "在线调用失败；本地图库和收录未受影响");
        }
        finally
        {
            if (_inspectedItem is { IsExternal: false, DeletedAt: null })
                InspectorOnlineAiButton.IsEnabled = true;
        }
    }

    private async Task ShowSimilarImagesAsync()
    {
        if (_inspectedItem is not { IsExternal: false } item)
        {
            ToastService.Show(this, "请先选择主图库中的一张图片");
            return;
        }
        await using var provider = new LocalClipAiProvider(_repository.Paths.Models);
        try
        {
            var service = new SimilaritySearchService(
                _repository,
                new AiProviderRegistry([provider]));
            var matches = await service.SearchByImageAsync(
                item.Id,
                LocalClipAiProvider.ProviderId,
                LocalClipAiProvider.ModelVersion,
                100);
            var results = new List<(GalleryItem Item, float Score)>();
            foreach (var match in matches)
            {
                if (await _repository.GetGalleryItemAsync(match.ItemId) is { } galleryItem)
                    results.Add((galleryItem, match.Score));
            }
            if (results.Count == 0)
            {
                ToastService.Show(this, "还没有相似图片向量，请先完成后台分析");
                return;
            }
            var window = new SimilarityResultsWindow(this, _repository, results);
            window.ItemRequested += selected =>
            {
                OpenInspector(GalleryEntry.FromLibrary(selected), null);
                window.Close();
            };
            window.Show();
        }
        catch (Exception ex)
        {
            AppLog.Warning("similarity-ui", "Similarity results could not be shown.", ex);
            ToastService.Show(this, $"相似搜索不可用：{ex.Message}");
        }
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
