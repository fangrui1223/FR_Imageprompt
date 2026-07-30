using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private GalleryLayoutVisualAnchor? _inspectorResizeAnchor;
    private InspectorEditSnapshot? _inspectorSnapshot;
    private InspectorEditSnapshot? _inspectorUndoSnapshot;
    private bool _populatingInspector;
    private bool _inspectorPromptDirty;
    private readonly SemaphoreSlim _inspectorSwitchGate = new(1, 1);
    private int _inspectorSwitchGeneration;
    private DispatcherTimer? _inspectorResizeTimer;
    private DateTimeOffset _lastTransparentInspectorNoticeAt;
    private readonly DispatcherTimer _inspectorSavedTimer = new()
    {
        Interval = TimeSpan.FromSeconds(10)
    };

    private void OpenInspector(GalleryCardViewModel card)
    {
        QueueOpenInspector(card.Item, card.Thumbnail);
    }

    private void OpenInspector(GalleryEntry item, BitmapSource? thumbnail)
    {
        QueueOpenInspector(item, thumbnail);
    }

    private void QueueOpenInspector(GalleryEntry item, BitmapSource? thumbnail)
    {
        var generation = Interlocked.Increment(ref _inspectorSwitchGeneration);
        _ = OpenInspectorAsync(item, thumbnail, generation);
    }

    private async Task OpenInspectorAsync(
        GalleryEntry item,
        BitmapSource? thumbnail,
        int generation)
    {
        await _inspectorSwitchGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _inspectorSwitchGeneration)) return;
            item = await EnsureFullEntryAsync(item) ?? item;
            if (generation != Volatile.Read(ref _inspectorSwitchGeneration)) return;
            if (_inspectedItem is { } same && same.Id == item.Id && _inspectorVisible)
            {
                if (thumbnail is not null)
                {
                    _inspectedThumbnail = thumbnail;
                    InspectorPreview.Source = thumbnail;
                }
                return;
            }

            if (_inspectedItem is { } current && current.Id != item.Id)
            {
                if (!await SaveInspectorOrdinaryFieldsAsync())
                {
                    RestoreInspectorSelection(current.Id);
                    return;
                }
                if (!await ResolveDirtyPromptBeforeSwitchAsync())
                {
                    RestoreInspectorSelection(current.Id);
                    return;
                }
            }

            if (generation != Volatile.Read(ref _inspectorSwitchGeneration)) return;
            _inspectedItem = item;
            _inspectedThumbnail = thumbnail;
            PopulateInspector(item, thumbnail);
            SetInspectorVisibility(true);
        }
        finally
        {
            _inspectorSwitchGate.Release();
        }
    }

    private void PopulateInspector(GalleryEntry item, BitmapSource? thumbnail)
    {
        _populatingInspector = true;
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
        InspectorAspectRatioText.Text = ImageAspectRatioFormatter.Format(item.Width, item.Height);
        var categoryChoices = new List<CategoryChoice> { new(0, "未分类") };
        categoryChoices.AddRange(_categories.Select(category => new CategoryChoice(category.Id, category.Name)));
        InspectorCategoryEditor.ItemsSource = categoryChoices;
        InspectorCategoryEditor.SelectedItem = categoryChoices.FirstOrDefault(choice =>
            choice.Id == (item.CategoryId ?? 0));
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
        InspectorCategoryEditor.IsEnabled = editable;
        InspectorSaveButton.IsEnabled = editable;
        InspectorOnlineAiButton.IsEnabled = editable;
        InspectorConfirmAiButton.IsEnabled = editable && !string.IsNullOrWhiteSpace(_inspectorAiDraft);
        InspectorRejectAiButton.IsEnabled = false;
        _inspectorSnapshot = new InspectorEditSnapshot(
            item.Id,
            item.CategoryId,
            item.Tags,
            item.Notes,
            item.Prompt);
        _inspectorUndoSnapshot = null;
        _inspectorPromptDirty = false;
        HideInspectorSaveStatus();
        UpdateInspectorPinVisual();
        _populatingInspector = false;
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
            if (!current.HasFullMetadata && _inspectedItem.HasFullMetadata)
            {
                current = MergeBrowseStateWithFullMetadata(current, _inspectedItem);
            }
            _inspectedItem = current;
            var card = Rows.SelectMany(row => row.Items).FirstOrDefault(candidate => candidate.Id == current.Id);
            if (card?.Thumbnail is not null) _inspectedThumbnail = card.Thumbnail;
            PopulateInspector(current, _inspectedThumbnail);
            return;
        }

        if (_inspectorVisible) SetInspectorVisibility(false);
    }

    private void SetInspectorVisibility(bool visible)
    {
        if (visible && _transparentMode)
        {
            ShowTransparentInspectorNotice();
            return;
        }
        if (_inspectorVisible == visible) return;

        var anchor = CaptureLayoutVisualAnchor();
        _inspectorVisible = visible;
        if (visible)
        {
            var width = ResponsiveInspectorWidth(_settings.InspectorWidth);
            InspectorColumn.Width = new GridLength(width, GridUnitType.Pixel);
            InspectorSplitterColumn.Width = new GridLength(6, GridUnitType.Pixel);
            InspectorSplitter.Visibility = Visibility.Visible;
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorPanel.Opacity = 1;
            InspectorPanel.IsHitTestVisible = true;
        }
        else
        {
            InspectorPanel.IsHitTestVisible = false;
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorPanel.Opacity = 0;
            InspectorSplitter.Visibility = Visibility.Collapsed;
            InspectorSplitterColumn.Width = new GridLength(0);
            InspectorColumn.Width = new GridLength(0);
        }

        MainContentGrid.UpdateLayout();
        ReflowGalleryForLayoutPreference(anchor);
    }

    private void ShowTransparentInspectorNotice()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastTransparentInspectorNoticeAt < TimeSpan.FromSeconds(1)) return;
        _lastTransparentInspectorNoticeAt = now;
        ToastService.Show(this, "请先退出透明模式查看详情");
    }

    private void InspectorPinClick(object sender, RoutedEventArgs e)
    {
        ToastService.Show(this, "详情已默认跟随当前选择");
    }

    private void UpdateInspectorPinVisual()
    {
        if (InspectorPinButton is null) return;
        _settings.InspectorPinned = false;
        InspectorPinButton.Content = "跟随选择";
    }

    private async void CloseInspectorClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveInspectorOrdinaryFieldsAsync())
        {
            InspectorTagsEditor.Focus();
            return;
        }
        if (!await ResolveDirtyPromptBeforeSwitchAsync())
        {
            InspectorPromptEditor.Focus();
            return;
        }
        SetInspectorVisibility(false);
    }

    private void OpenCardDetailsClick(object sender, RoutedEventArgs e)
    {
        var itemId = (sender as FrameworkElement)?.Tag as long?;
        if (itemId is null && (sender as FrameworkElement)?.DataContext is GalleryCardViewModel card)
        {
            itemId = card.Id;
        }
        if (itemId is null)
        {
            itemId = GetCardFromMenuSender(sender)?.Id ?? _selectionFocusId;
        }
        if (itemId is null) return;
        if (!_selectedItemIds.Contains(itemId.Value)) SelectSingle(itemId.Value);
        InspectItem(itemId.Value);
        e.Handled = true;
    }

    private void InspectorSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        _inspectorResizeAnchor = CaptureLayoutVisualAnchor();
    }

    private void InspectorSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        _inspectorResizeTimer ??= CreateInspectorResizeTimer();
        _inspectorResizeTimer.Stop();
        _inspectorResizeTimer.Start();
    }

    private DispatcherTimer CreateInspectorResizeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            MainContentGrid.UpdateLayout();
            ReflowGalleryForLayoutPreference(_inspectorResizeAnchor);
        };
        return timer;
    }

    private void InspectorSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _inspectorResizeTimer?.Stop();
        var width = AppSettings.NormalizeInspectorWidth(InspectorColumn.ActualWidth);
        _settings.InspectorWidth = width;
        _settings.Save();
        InspectorColumn.Width = new GridLength(ResponsiveInspectorWidth(width), GridUnitType.Pixel);
        MainContentGrid.UpdateLayout();
        ReflowGalleryForLayoutPreference(_inspectorResizeAnchor);
        _inspectorResizeAnchor = null;
    }

    private double ResponsiveInspectorWidth(double requested)
    {
        var normalized = AppSettings.NormalizeInspectorWidth(requested);
        if (ActualWidth >= 820) return Math.Min(normalized, ActualWidth - 380);
        return Math.Max(240, ActualWidth * 0.44);
    }

    private async void SaveInspectorClick(object sender, RoutedEventArgs e)
    {
        await SaveInspectorPromptAsync();
    }

    private async void InspectorCategorySelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_populatingInspector) return;
        await SaveInspectorOrdinaryFieldsAsync();
    }

    private async void InspectorOrdinaryFieldLostFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_populatingInspector) return;
        await SaveInspectorOrdinaryFieldsAsync();
    }

    private void InspectorPromptTextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_populatingInspector || _inspectorSnapshot is null) return;
        _inspectorPromptDirty = !string.Equals(
            InspectorPromptEditor.Text.Trim(),
            _inspectorSnapshot.Prompt,
            StringComparison.Ordinal);
        InspectorSaveButton.Content = _inspectorPromptDirty ? "保存提示词 ●" : "保存提示词";
    }

    private async Task<bool> SaveInspectorOrdinaryFieldsAsync(bool showUndo = true)
    {
        if (_populatingInspector
            || _inspectedItem is not { IsExternal: false, DeletedAt: null } item
            || _inspectorSnapshot is not { } before
            || before.ItemId != item.Id)
        {
            return true;
        }

        var categoryId = (InspectorCategoryEditor.SelectedItem as CategoryChoice)?.Id;
        if (categoryId == 0) categoryId = null;
        var after = before with
        {
            CategoryId = categoryId,
            Tags = InspectorTagsEditor.Text.Trim(),
            Notes = InspectorNotesEditor.Text.Trim()
        };
        if (before.CategoryId == after.CategoryId
            && string.Equals(before.Tags, after.Tags, StringComparison.Ordinal)
            && string.Equals(before.Notes, after.Notes, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            await PersistInspectorSnapshotAsync(after);
            _inspectorSnapshot = after;
            if (showUndo)
            {
                _inspectorUndoSnapshot = before;
                ShowInspectorSavedStatus();
            }
            ApplyInspectorSnapshotToCurrentEntry(after);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("inspector-autosave", "Inspector ordinary fields could not be saved.", ex);
            ToastService.Show(this, $"自动保存失败：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> SaveInspectorPromptAsync()
    {
        if (_inspectedItem is not { IsExternal: false, DeletedAt: null } item
            || _inspectorSnapshot is not { } before
            || before.ItemId != item.Id)
        {
            return false;
        }

        if (!await SaveInspectorOrdinaryFieldsAsync()) return false;
        before = _inspectorSnapshot ?? before;
        var after = before with { Prompt = InspectorPromptEditor.Text.Trim() };
        InspectorSaveButton.IsEnabled = false;
        try
        {
            await PersistInspectorSnapshotAsync(after);
            _inspectorSnapshot = after;
            _inspectorPromptDirty = false;
            InspectorSaveButton.Content = "保存提示词";
            ApplyInspectorSnapshotToCurrentEntry(after);
            ToastService.Show(this, "提示词已保存");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("inspector-prompt-save", "Inspector prompt could not be saved.", ex);
            ToastService.Show(this, $"保存提示词失败：{ex.Message}");
            return false;
        }
        finally
        {
            InspectorSaveButton.IsEnabled = _inspectedItem is { IsExternal: false, DeletedAt: null };
        }
    }

    private async Task<bool> ResolveDirtyPromptBeforeSwitchAsync()
    {
        if (!_inspectorPromptDirty) return true;
        var choice = MessageBox.Show(
            this,
            "当前提示词尚未保存。\n\n是：保存并切换\n否：放弃修改并切换\n取消：留在当前图片",
            "提示词尚未保存",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return await SaveInspectorPromptAsync();
        if (_inspectorSnapshot is { } snapshot)
        {
            _populatingInspector = true;
            InspectorPromptEditor.Text = snapshot.Prompt;
            _populatingInspector = false;
            _inspectorPromptDirty = false;
            InspectorSaveButton.Content = "保存提示词";
        }
        return true;
    }

    private async void UndoInspectorSaveClick(object sender, RoutedEventArgs e)
    {
        if (_inspectorUndoSnapshot is not { } undo
            || _inspectedItem?.Id != undo.ItemId)
        {
            HideInspectorSaveStatus();
            return;
        }

        try
        {
            await PersistInspectorSnapshotAsync(undo);
            _inspectorSnapshot = undo;
            _inspectorUndoSnapshot = null;
            ApplyInspectorSnapshotToCurrentEntry(undo);
            _populatingInspector = true;
            InspectorTagsEditor.Text = undo.Tags;
            InspectorNotesEditor.Text = undo.Notes;
            InspectorCategoryEditor.SelectedItem =
                (InspectorCategoryEditor.ItemsSource as IEnumerable<CategoryChoice>)
                ?.FirstOrDefault(choice => choice.Id == (undo.CategoryId ?? 0));
            _populatingInspector = false;
            HideInspectorSaveStatus();
            ToastService.Show(this, "已撤销自动保存");
        }
        catch (Exception ex)
        {
            AppLog.Warning("inspector-undo", "Inspector autosave could not be undone.", ex);
            ToastService.Show(this, $"撤销失败：{ex.Message}");
        }
    }

    private async Task PersistInspectorSnapshotAsync(InspectorEditSnapshot snapshot)
    {
        await _repository.UpdateItemOrganizationAsync(
            snapshot.ItemId,
            snapshot.Prompt,
            snapshot.Tags,
            snapshot.Notes,
            snapshot.CategoryId);
    }

    private void ApplyInspectorSnapshotToCurrentEntry(InspectorEditSnapshot snapshot)
    {
        if (_inspectedItem is not { } current || current.Id != snapshot.ItemId) return;
        var categoryName = snapshot.CategoryId is { } categoryId
            ? _categories.FirstOrDefault(category => category.Id == categoryId)?.Name ?? current.CategoryName
            : "";
        var updated = current with
        {
            CategoryId = snapshot.CategoryId,
            CategoryName = categoryName,
            Tags = snapshot.Tags,
            Notes = snapshot.Notes,
            Prompt = snapshot.Prompt
        };
        _inspectedItem = updated;
        var index = _items.FindIndex(entry => entry.Id == updated.Id);
        if (index >= 0) _items[index] = updated;
        foreach (var card in Rows.SelectMany(row => row.Items).Where(card => card.Id == updated.Id))
        {
            card.UpdateFrom(
                updated,
                card.LayoutX,
                card.LayoutY,
                card.LayoutWidth,
                card.ImageHeight,
                card.IsSelected);
        }
        InspectorMetaText.Text = string.Join(
            "  ·  ",
            new[]
            {
                string.IsNullOrWhiteSpace(updated.CategoryName) ? "未分类" : updated.CategoryName,
                updated.IsFavorite ? "已收藏" : null
            }.Where(value => value is not null));
    }

    private void RestoreInspectorSelection(long itemId)
    {
        _selectedItemIds.Clear();
        _selectedItemIds.Add(itemId);
        _selectionAnchorId = itemId;
        _selectionFocusId = itemId;
        ApplySelectionState();
        ScrollSelectionIntoView(itemId);
    }

    private void ShowInspectorSavedStatus()
    {
        InspectorSaveStatusText.Text = "已保存";
        InspectorUndoButton.Visibility = Visibility.Visible;
        _inspectorSavedTimer.Stop();
        _inspectorSavedTimer.Tick -= InspectorSavedTimerTick;
        _inspectorSavedTimer.Tick += InspectorSavedTimerTick;
        _inspectorSavedTimer.Start();
    }

    private void InspectorSavedTimerTick(object? sender, EventArgs e)
    {
        _inspectorSavedTimer.Stop();
        HideInspectorSaveStatus();
    }

    private void HideInspectorSaveStatus()
    {
        _inspectorSavedTimer.Stop();
        InspectorSaveStatusText.Text = "";
        InspectorUndoButton.Visibility = Visibility.Collapsed;
    }

    private sealed record InspectorEditSnapshot(
        long ItemId,
        long? CategoryId,
        string Tags,
        string Notes,
        string Prompt);

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

    private async void AddInspectorToBoardClick(object sender, RoutedEventArgs e)
    {
        if (_inspectedItem is null) return;
        await AddEntriesToBoardAsync([_inspectedItem]);
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
