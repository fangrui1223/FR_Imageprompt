using PromptVault.Core;

namespace PromptVault.App.Services;

internal sealed class BoardPendingSaves(LibraryRepository repository)
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(long Board, long Item), BoardItemUpdate> _items = [];
    private readonly Dictionary<(long Board, long Note), BoardNoteUpdate> _notes = [];
    private readonly Dictionary<long, ViewSave> _views = [];
    private sealed record ViewSave(double X, double Y, double Zoom);

    public bool HasPending { get { lock (_sync) return _items.Count + _notes.Count + _views.Count > 0; } }

    public void EnqueueItems(long boardId, IEnumerable<BoardItemUpdate> updates)
    {
        lock (_sync) foreach (var update in updates) _items[(boardId, update.Id)] = update;
    }

    public void EnqueueNotes(long boardId, IEnumerable<BoardNoteUpdate> updates)
    {
        lock (_sync) foreach (var update in updates) _notes[(boardId, update.Id)] = update;
    }

    public void EnqueueView(long boardId, double x, double y, double zoom)
    {
        lock (_sync) _views[boardId] = new ViewSave(x, y, zoom);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (HasPending)
            {
                KeyValuePair<(long Board, long Item), BoardItemUpdate>[] items;
                KeyValuePair<(long Board, long Note), BoardNoteUpdate>[] notes;
                KeyValuePair<long, ViewSave>[] views;
                lock (_sync) { items = _items.ToArray(); notes = _notes.ToArray(); views = _views.ToArray(); }
                foreach (var group in items.GroupBy(entry => entry.Key.Board))
                {
                    await repository.UpdateBoardItemsAsync(group.Key, group.Select(entry => entry.Value).ToArray(), cancellationToken).ConfigureAwait(false);
                    lock (_sync) foreach (var entry in group)
                        if (_items.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value)) _items.Remove(entry.Key);
                }
                foreach (var group in notes.GroupBy(entry => entry.Key.Board))
                {
                    await repository.UpdateBoardNotesAsync(group.Key, group.Select(entry => entry.Value).ToArray(), cancellationToken).ConfigureAwait(false);
                    lock (_sync) foreach (var entry in group)
                        if (_notes.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value)) _notes.Remove(entry.Key);
                }
                foreach (var entry in views)
                {
                    await repository.UpdateBoardViewAsync(entry.Key, entry.Value.X, entry.Value.Y, entry.Value.Zoom, cancellationToken).ConfigureAwait(false);
                    lock (_sync)
                        if (_views.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value)) _views.Remove(entry.Key);
                }
            }
        }
        finally { _gate.Release(); }
    }
}
