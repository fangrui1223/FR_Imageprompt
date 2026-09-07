using Microsoft.Data.Sqlite;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task<IReadOnlyDictionary<string, bool>> RunM11PersistenceSmokeAsync(AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M11 保存故障烟测");
        if (!_repository.Paths.Root.Contains($"{Path.DirectorySeparatorChar}m11-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("M11 故障注入仅允许独立 M11 合成图库。");
        await EnsureBoardLoadedAsync();
        await WaitForLayoutAsync();
        var result = new Dictionary<string, bool>();
        var boardId = CurrentBoardId;
        var second = await _repository.CreateBoardAsync("M11-switch-" + Guid.NewGuid().ToString("N"));
        var note = await _repository.AddBoardNoteAsync(boardId, "M11-original", 40, 40);
        _notes.Add(note);
        var changed = note with { Text = "M11-retained edit" };
        ReplaceNote(changed);
        await M11SqlAsync("CREATE TRIGGER m11_reject_notes BEFORE UPDATE ON board_notes BEGIN SELECT RAISE(ABORT, 'M11 injected note failure'); END;");
        try
        {
            result["noteFailureReported"] = !await SaveNoteAsync(changed, "便签内容已保存")
                && BoardStatusText.Text.Contains("保存失败", StringComparison.Ordinal) && _pendingSaves.HasPending;
            await LoadBoardAsync(second.Id);
            result["switchBlockedAndEditRetained"] = CurrentBoardId == boardId && _notes.Single(value => value.Id == note.Id).Text == changed.Text;
            var closing = new System.ComponentModel.CancelEventArgs();
            OnClosing(closing);
            await WaitForLayoutAsync();
            result["closeBlocked"] = closing.Cancel && !_closeApproved && IsVisible;
            result["applicationExitBlocked"] = !await PrepareForApplicationExitAsync();
        }
        finally { await M11SqlAsync("DROP TRIGGER IF EXISTS m11_reject_notes;"); }
        await RetryBoardSaveAsync();
        result["noteRetryPersisted"] = !_pendingSaves.HasPending
            && (await _repository.GetBoardNotesAsync(boardId)).Single(value => value.Id == note.Id).Text == changed.Text;

        var item = _items.First(value => _realized.ContainsKey(value.Id));
        _selectedIds.Clear();
        _selectedIds.Add(item.Id);
        _selectedNoteIds.Clear();
        RenderVisibleItems();
        await WaitForLayoutAsync();
        BeginCropMode();
        if (!_cropModeActive) throw new InvalidOperationException("M11 裁剪图片未就绪。");
        _cropViewport = BoardCropEngine.ZoomAt(_cropViewport, _cropMinimumCover, 0.5, 0.5, 1.2);
        _cropChanged = true;
        await M11SqlAsync("CREATE TRIGGER m11_reject_items BEFORE UPDATE ON board_items BEGIN SELECT RAISE(ABORT, 'M11 injected image failure'); END;");
        try
        {
            var historyCount = _undo.Count;
            result["cropFailureStaysEditable"] = !await CommitCropModeAsync()
                && _cropModeActive && _pendingSaves.HasPending && BoardStatusText.Text.Contains("保存失败", StringComparison.Ordinal)
                && _undo.Count == historyCount;
        }
        finally { await M11SqlAsync("DROP TRIGGER IF EXISTS m11_reject_items;"); }
        result["cropRetryCommitted"] = await CommitCropModeAsync() && !_cropModeActive && !_pendingSaves.HasPending;

        var beforeUndo = SnapshotScene();
        var undoCount = _undo.Count;
        await M11SqlAsync("CREATE TRIGGER m11_reject_restore BEFORE INSERT ON board_notes BEGIN SELECT RAISE(ABORT, 'M11 injected restore failure'); END;");
        try
        {
            await ExecuteBoardCommandAsync(BoardCommandId.Undo);
            result["failedUndoRetainsStacksAndScene"] = _undo.Count == undoCount
                && beforeUndo.Items.SequenceEqual(_items) && beforeUndo.Notes.SequenceEqual(_notes);
        }
        finally { await M11SqlAsync("DROP TRIGGER IF EXISTS m11_reject_restore;"); }
        await ExecuteBoardCommandAsync(BoardCommandId.Undo);
        await ExecuteBoardCommandAsync(BoardCommandId.Redo);
        result["undoRedoRoundTrip"] = _undo.Count == undoCount && beforeUndo.Items.SequenceEqual(_items) && beforeUndo.Notes.SequenceEqual(_notes);
        await LoadBoardAsync(second.Id);
        result["switchSucceedsAfterRetry"] = CurrentBoardId == second.Id;
        await LoadBoardAsync(boardId);
        await _repository.DeleteBoardAsync(second.Id);
        result["exitPreparationSucceedsAfterRetry"] = await PrepareForApplicationExitAsync();
        return result;
    }

    private async Task M11SqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_repository.Paths.Database};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
