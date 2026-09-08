using System.Windows;
using System.Windows.Input;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class BoardWindow
{
    private readonly BoardPendingSaves _pendingSaves;
    private string? _saveError;
    private bool _boardBoundaryActive;
    private bool _boardLoaded;
    private bool _closeApproved;

    internal async Task<bool> PrepareForApplicationExitAsync()
    {
        if (_boardBoundaryActive || _boardCommandActive)
        {
            SetStatus("画板操作尚未完成，请稍后重试退出");
            return false;
        }
        _boardBoundaryActive = true;
        try { return !_boardLoaded || await PrepareBoardBoundaryAsync(); }
        finally { _boardBoundaryActive = false; }
    }

    internal void ApproveApplicationExit() => _closeApproved = true;

    private async Task<bool> FlushPendingSavesAsync(string? successStatus = null)
    {
        try
        {
            await _pendingSaves.FlushAsync();
            _saveError = null;
            RetryBoardSaveButton.Visibility = Visibility.Collapsed;
            if (successStatus is not null) SetStatus(successStatus);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-save", "Board changes remain pending for retry.", ex);
            _saveError = $"保存失败，修改已保留：{ex.Message}";
            RetryBoardSaveButton.Visibility = Visibility.Visible;
            SetStatus(_saveError);
            return false;
        }
    }

    private async Task<bool> PrepareBoardBoundaryAsync()
    {
        if (_editingNoteId is { } noteId) await CommitNoteEditingAsync(noteId);
        if (_cropModeActive && !await CommitCropModeAsync()) return false;
        // An interrupted preview must not leak into the next board or a closing window.
        ExitTransformMode();
        CancelSelectionTranslation();
        _dragItemId = null;
        _itemDragActive = false;
        _dragNoteId = null;
        _noteDragActive = false;
        _noteGestureSnapshot = null;
        return await PersistViewNowAsync(force: true);
    }

    private async void RetryBoardSaveClick(object sender, RoutedEventArgs e) => await RetryBoardSaveAsync();

    private async Task RetryBoardSaveAsync()
    {
        if (_boardBoundaryActive) return;
        _boardBoundaryActive = true;
        var enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            if (await PrepareBoardBoundaryAsync()) SetStatus("所有修改已保存");
        }
        finally { IsEnabled = enabled; _boardBoundaryActive = false; }
    }

    private async Task CloseAfterSavingAsync()
    {
        _boardBoundaryActive = true;
        var enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            CancelCameraAnimation();
            CancelProgressiveFocusLoad();
            _viewSaveCancellation?.Cancel();
            if (_boardLoaded && !await PrepareBoardBoundaryAsync()) return;
            _closeApproved = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-close", "Board remains open because saving failed.", ex);
            SetStatus($"未关闭画板：{ex.Message}");
        }
        finally { IsEnabled = enabled; _boardBoundaryActive = false; }
    }
}
