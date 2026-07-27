using System.Windows;
using PromptVault.Core;

namespace PromptVault.App.Services;

internal sealed record BoardAddItem(long CollectionItemId, int Width, int Height);

internal sealed class BoardWorkspaceService
{
    private readonly LibraryRepository _repository;
    private readonly Dictionary<long, BoardWindow> _windows = [];
    private long? _lastBoardId;

    public BoardWorkspaceService(LibraryRepository repository)
    {
        _repository = repository;
    }

    public async Task<BoardWindow> OpenAsync(Window? owner = null, long? boardId = null)
    {
        var boards = await _repository.GetBoardsAsync();
        var targetId = boardId
            ?? (_lastBoardId is { } last && boards.Any(board => board.Id == last) ? (long?)last : null)
            ?? boards.FirstOrDefault()?.Id;
        if (targetId is null)
        {
            targetId = (await _repository.CreateBoardAsync("灵感画板")).Id;
        }

        if (_windows.TryGetValue(targetId.Value, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        var window = new BoardWindow(_repository, this, targetId.Value);
        _windows[targetId.Value] = window;
        _lastBoardId = targetId.Value;
        window.Closed += (_, _) => _windows.Remove(window.CurrentBoardId);
        window.Show();
        return window;
    }

    public async Task AddAsync(
        IReadOnlyList<BoardAddItem> items,
        Window? owner = null)
    {
        if (items.Count == 0) return;
        var window = await OpenAsync(owner);
        await window.AddCollectionItemsAsync(items);
        window.Activate();
    }

    public void BoardChanged(BoardWindow window, long previousBoardId, long currentBoardId)
    {
        _windows.Remove(previousBoardId);
        _windows[currentBoardId] = window;
        _lastBoardId = currentBoardId;
    }
}
