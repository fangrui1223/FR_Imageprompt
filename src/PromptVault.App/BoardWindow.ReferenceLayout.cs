using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private bool _referenceLocked;
    private string ReferenceLockKey => AppSettings.BoardReferenceKey(_repository.Paths.Root, CurrentBoardId);

    private bool RejectReferenceMutation()
    {
        if (!_referenceLocked) return false;
        SetStatus("参考锁定中；按 Ctrl+L 或点击“解锁”后可编辑");
        return true;
    }

    private async void ReferenceLockClick(object sender, RoutedEventArgs e)
    {
        await ExecuteBoardCommandAsync(BoardCommandId.ToggleReferenceLock);
        UpdateReferenceLockUi();
    }

    private async Task ToggleReferenceLockAsync()
    {
        var enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            if (!_referenceLocked)
            {
                EndPan();
                _rightGesture = null;
                _rightTargetId = null;
                _rightWindowDragStarted = false;
                _rightCanvasPanStarted = false;
                _rightControlPressed = false;
                await CommitNotePropertyGestureAsync();
                if (NoteColorPopup.IsOpen) NoteColorPopup.IsOpen = false;
                if (!await PrepareBoardBoundaryAsync()) return;
                EndMarquee();
                Mouse.Capture(null);
            }
            var key = ReferenceLockKey;
            var value = !_referenceLocked;
            if (value) _settings.ReferenceLockedBoards.Add(key);
            else _settings.ReferenceLockedBoards.Remove(key);
            try { _settings.Save(); }
            catch
            {
                if (value) _settings.ReferenceLockedBoards.Remove(key);
                else _settings.ReferenceLockedBoards.Add(key);
                throw;
            }
            _referenceLocked = value;
            SetStatus(value ? "参考锁定已开启；可缩放、平移和浏览" : "参考锁定已解除");
        }
        finally
        {
            IsEnabled = enabled;
            RenderVisibleItems();
            UpdateReferenceLockUi();
        }
    }

    private void UpdateReferenceLockUi()
    {
        ReferenceLockButton.IsChecked = _referenceLocked;
        ReferenceLockBadge.Visibility = _referenceLocked ? Visibility.Visible : Visibility.Collapsed;
        NotePropertiesPanel.IsEnabled = !_referenceLocked;
        InspectorGeneralPanel.IsEnabled = !_referenceLocked;
        UpdateUndoButtons();
        RefreshOpenContextMenuState();
    }

    private async Task ArrangeSelectedImagesAsync(BoardImageLayoutKind kind)
    {
        if (_referenceLocked) return;
        if (_cropModeActive && !await CommitCropModeAsync()) return;
        var before = SnapshotScene();
        var selected = _items.Where(item => _selectedIds.Contains(item.Id)).ToArray();
        var arranged = BoardImageLayout.Arrange(selected, kind);
        var changes = selected.Zip(arranged)
            .Where(pair => Math.Abs(pair.First.X - pair.Second.X) > 0.000001
                || Math.Abs(pair.First.Y - pair.Second.Y) > 0.000001)
            .Select(pair => pair.Second).ToArray();
        if (changes.Length == 0) { SetStatus("所选图片已按此方式排列"); return; }
        foreach (var item in changes) ReplaceItem(item);
        BoardViewportEngine.RefreshBounds(_items, changes.Select(item => item.Id));
        CommitSceneHistorySnapshot(before);
        RenderVisibleItems();
        _pendingSaves.EnqueueItems(CurrentBoardId, changes.Select(ToUpdate));
        await FlushPendingSavesAsync("图片排版已保存；Ctrl+Z 可一步撤销");
    }
}
