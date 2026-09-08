using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private async void BeginNoteEditing(long noteId, bool isNew = false)
    {
        if (_referenceLocked) return;
        if (_editingNoteId == noteId) return;
        if (_editingNoteId is { } previousId) await CommitNoteEditingAsync(previousId);
        var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
        if (note is null) return;
        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        _selectedNoteIds.Add(noteId);
        _editingNoteId = noteId;
        _editingNoteOriginalText = note.Text;
        _noteEditSnapshot = isNew ? null : SnapshotScene();
        if (isNew) _newUnconfirmedNoteId = noteId;
        RenderVisibleItems();
        if (!_realizedNotes.TryGetValue(noteId, out var visual)) return;
        visual.Editor.IsReadOnly = false;
        visual.Editor.Focus();
        visual.Editor.SelectAll();
    }

    private async Task CommitNoteEditingAsync(long noteId)
    {
        if (_editingNoteId != noteId) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
        if (note is null || !_realizedNotes.TryGetValue(noteId, out var visual))
        {
            ClearNoteEditingState();
            Keyboard.Focus(BoardViewport);
            return;
        }
        var text = visual.Editor.Text;
        var changed = !string.Equals(note.Text, text, StringComparison.Ordinal);
        var historySnapshot = _noteEditSnapshot;
        note = note with { Text = text };
        if (_newUnconfirmedNoteId == noteId)
        {
            _newUnconfirmedNoteId = null;
            changed = true;
        }
        ReplaceNote(note);
        if (changed && historySnapshot is { } before) CommitSceneHistorySnapshot(before);

        // Release keyboard and pointer ownership before the first await so the same
        // click that ends editing can continue as a canvas/item/note gesture.
        ClearNoteEditingState();
        RenderVisibleItems();
        Keyboard.Focus(BoardViewport);
        if (changed) await SaveNoteAsync(note, "便签内容已保存");
    }

    private async Task CancelNoteEditingAsync()
    {
        if (_editingNoteId is not { } noteId) return;
        _cancelingNoteEdit = true;
        try
        {
            if (_newUnconfirmedNoteId == noteId)
            {
                await _repository.DeleteBoardNotesAsync(CurrentBoardId, [noteId]);
                _notes.RemoveAll(note => note.Id == noteId);
                _selectedNoteIds.Remove(noteId);
                if (_undo.Count > 0) _undo.Pop();
                UpdateUndoButtons();
                SetStatus("已取消新建便签");
            }
            else if (_editingNoteOriginalText is { } original)
            {
                var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
                if (note is not null) ReplaceNote(note with { Text = original });
                SetStatus("已取消编辑");
            }
            ClearNoteEditingState();
            Keyboard.Focus(BoardViewport);
            RenderVisibleItems();
        }
        finally
        {
            _cancelingNoteEdit = false;
        }
    }

    private void ClearNoteEditingState()
    {
        _editingNoteId = null;
        _editingNoteOriginalText = null;
        _newUnconfirmedNoteId = null;
        _noteEditSnapshot = null;
    }

    private async void NoteEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: BoardNoteVisualTag { NoteId: var noteId } }) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await CancelNoteEditingAsync();
            return;
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await CommitNoteEditingAsync(noteId);
            Keyboard.Focus(BoardViewport);
        }
    }

    private async Task FitSelectedNoteToContentAsync()
    {
        if (_referenceLocked) return;
        if (SingleSelectedNoteId is not { } noteId) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
        if (note is null) return;
        var before = SnapshotScene();
        var fitted = FitNoteRecordToContent(note);
        ReplaceNote(fitted);
        if (fitted.Width != note.Width || fitted.Height != note.Height)
        {
            CommitSceneHistorySnapshot(before);
            await SaveNoteAsync(fitted, "便签已适合文字内容");
        }
        RenderVisibleItems();
    }

    private BoardNoteRecord FitNoteRecordToContent(BoardNoteRecord note)
    {
        var style = BoardNoteStyleCodec.Decode(note.ColorStyle);
        var measurement = new TextBlock
        {
            Text = string.IsNullOrEmpty(note.Text) ? " " : note.Text,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily,
            FontSize = style.FontSize,
            LineHeight = style.FontSize * style.LineSpacing,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        measurement.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return note with
        {
            Width = Math.Clamp(Math.Ceiling(measurement.DesiredSize.Width + 24), 32, 20_000),
            Height = Math.Clamp(
                Math.Ceiling(measurement.DesiredSize.Height + style.VerticalPadding * 2),
                24,
                20_000)
        };
    }

    private static BoardNoteRecord ScaleNote(
        BoardNoteRecord note,
        BoardWorldRect originalBounds,
        BoardWorldRect targetBounds)
    {
        var scaleX = targetBounds.Width / Math.Max(0.001, originalBounds.Width);
        var scaleY = targetBounds.Height / Math.Max(0.001, originalBounds.Height);
        return note with
        {
            X = targetBounds.X + (note.X - originalBounds.X) * scaleX,
            Y = targetBounds.Y + (note.Y - originalBounds.Y) * scaleY,
            Width = Math.Max(32, note.Width * scaleX),
            Height = Math.Max(24, note.Height * scaleY)
        };
    }

    private async Task ApplyStyleToSelectedNotesAsync(
        Func<BoardNoteStyle, BoardNoteStyle> transform,
        string status,
        bool commitHistory = true)
    {
        if (_referenceLocked || _selectedNoteIds.Count == 0) return;
        var before = commitHistory ? SnapshotScene() : null;
        foreach (var id in _selectedNoteIds.ToArray())
        {
            var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
            if (note is null) continue;
            var style = transform(BoardNoteStyleCodec.Decode(note.ColorStyle)).Normalize();
            ReplaceNote(note with { ColorStyle = BoardNoteStyleCodec.Encode(style) });
        }
        if (before is not null) CommitSceneHistorySnapshot(before);
        RenderVisibleItems();
        await SaveSelectedNotesAsync(status);
    }

    private Task ToggleSelectedNoteBackgroundAsync() =>
        ApplyStyleToSelectedNotesAsync(
            style => style with { BackgroundEnabled = !style.BackgroundEnabled },
            "便签背景已更新");

    private Task AlignSelectedNotesAsync(BoardNoteTextAlignment alignment) =>
        ApplyStyleToSelectedNotesAsync(
            style => style with { Alignment = alignment },
            "便签对齐已保存");

    private Task ApplyNotePresetAsync(int slot) =>
        slot < 0 || slot >= _settings.BoardNotePresets.Count
            ? Task.CompletedTask
            : ApplyStyleToSelectedNotesAsync(
                _ => _settings.BoardNotePresets[slot],
                $"已应用便签预设 {slot + 1}");

    private async Task SaveNotePresetAsync(int slot)
    {
        if (SingleSelectedNoteId is not { } noteId
            || slot < 0
            || slot >= BoardNotePresetDefaults.SlotCount) return;
        var note = _notes.Single(candidate => candidate.Id == noteId);
        if (MessageBox.Show(
                this,
                $"用当前便签样式覆盖全局预设 {slot + 1}？",
                "保存便签预设",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _settings.BoardNotePresets[slot] = BoardNoteStyleCodec.Decode(note.ColorStyle);
        _settings.Save();
        SetStatus($"便签预设 {slot + 1} 已保存");
        await Task.CompletedTask;
    }

    private async Task DuplicateSelectedNotesAsync()
    {
        if (_selectedNoteIds.Count == 0) return;
        var originals = _notes.Where(note => _selectedNoteIds.Contains(note.Id)).ToArray();
        var before = SnapshotScene();
        var addedIds = new List<long>();
        foreach (var note in originals)
        {
            var added = await _repository.AddBoardNoteAsync(
                CurrentBoardId,
                note.Text,
                note.X + 24,
                note.Y + 24,
                note.Width,
                note.Height,
                note.ZIndex + 1,
                note.ColorStyle);
            _notes.Add(added);
            addedIds.Add(added.Id);
        }
        CommitSceneHistorySnapshot(before);
        _selectedNoteIds.Clear();
        _selectedNoteIds.UnionWith(addedIds);
        RenderVisibleItems();
        SetStatus(addedIds.Count > 1 ? $"已复制 {addedIds.Count} 个便签" : "便签已复制");
    }

    private async Task MoveSelectedNotesLayerAsync(int direction, bool extreme = false)
    {
        if (_selectedNoteIds.Count == 0) return;
        var before = SnapshotScene();
        var edge = _notes.Count == 0
            ? 0
            : direction > 0 ? _notes.Max(note => note.ZIndex) : _notes.Min(note => note.ZIndex);
        var next = edge;
        foreach (var id in _selectedNoteIds.ToArray())
        {
            var note = _notes.Single(candidate => candidate.Id == id);
            var z = extreme ? next += Math.Sign(direction) : note.ZIndex + Math.Sign(direction);
            ReplaceNote(note with { ZIndex = z });
        }
        CommitSceneHistorySnapshot(before);
        RenderVisibleItems();
        await SaveSelectedNotesAsync("便签层级已保存");
    }

    private void CloseInspectorIfSelectionChanged()
    {
        if (BoardInspector.Visibility == Visibility.Visible && SingleSelectedNoteId is null)
            CloseInspector();
    }
}
