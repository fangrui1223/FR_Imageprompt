using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private BoardSceneSnapshot? _moveSceneSnapshot;
    private HashSet<long> _moveItemIds = [];
    private HashSet<long> _moveNoteIds = [];

    private void SelectAllBoardContent()
    {
        _selectedIds.Clear();
        _selectedIds.UnionWith(_items.Select(item => item.Id));
        _selectedNoteIds.Clear();
        _selectedNoteIds.UnionWith(_notes.Select(note => note.Id));
        RenderVisibleItems();
        SetStatus($"已选择 {_selectedIds.Count} 张图片、{_selectedNoteIds.Count} 个便签");
    }

    private void SelectImageForPointer(long id, bool additive)
    {
        if (!additive && !_selectedIds.Contains(id)) _selectedNoteIds.Clear();
        var selected = BoardInteractionEngine.SelectItem(_items, _selectedIds, id, additive);
        _selectedIds.Clear();
        _selectedIds.UnionWith(selected);
    }

    private void SelectNoteForPointer(long id, bool additive)
    {
        if (additive)
        {
            if (!_selectedNoteIds.Add(id)) _selectedNoteIds.Remove(id);
        }
        else if (!_selectedNoteIds.Contains(id))
        {
            _selectedIds.Clear();
            _selectedNoteIds.Clear();
            _selectedNoteIds.Add(id);
        }
    }

    private void BeginSelectionTranslation()
    {
        _moveSceneSnapshot = SnapshotScene();
        _moveItemIds = _selectedIds.ToHashSet();
        _moveNoteIds = _selectedNoteIds.ToHashSet();
    }

    private void ApplySelectionTranslation(double dx, double dy)
    {
        if (_referenceLocked || _moveSceneSnapshot is not { } before) return;
        for (var index = 0; index < before.Items.Count; index++)
        {
            var item = before.Items[index];
            if (_moveItemIds.Contains(item.Id)) _items[index] = item with { X = item.X + dx, Y = item.Y + dy };
        }
        for (var index = 0; index < before.Notes.Count; index++)
        {
            var note = before.Notes[index];
            if (_moveNoteIds.Contains(note.Id)) _notes[index] = note with { X = note.X + dx, Y = note.Y + dy };
        }
        BoardViewportEngine.RefreshBounds(_items, _moveItemIds);
        RenderVisibleItems();
    }

    private async Task CompleteSelectionTranslationAsync()
    {
        if (_moveSceneSnapshot is not { } before) return;
        _moveSceneSnapshot = null;
        if (before.Items.SequenceEqual(_items) && before.Notes.SequenceEqual(_notes)) return;
        CommitSceneHistorySnapshot(before);
        // Queue both kinds before awaiting so a failed item write cannot lose pending notes.
        _pendingSaves.EnqueueItems(CurrentBoardId, _items.Where(item => _moveItemIds.Contains(item.Id)).Select(ToUpdate));
        _pendingSaves.EnqueueNotes(CurrentBoardId, _notes.Where(note => _moveNoteIds.Contains(note.Id)).Select(ToUpdate));
        await FlushPendingSavesAsync("所选内容位置已保存");
    }

    private void CancelSelectionTranslation()
    {
        if (_moveSceneSnapshot is not { } before) return;
        _moveSceneSnapshot = null;
        foreach (var item in before.Items.Where(item => _moveItemIds.Contains(item.Id))) ReplaceItem(item);
        foreach (var note in before.Notes.Where(note => _moveNoteIds.Contains(note.Id))) ReplaceNote(note);
        BoardViewportEngine.RefreshBounds(_items, _moveItemIds);
    }
}
