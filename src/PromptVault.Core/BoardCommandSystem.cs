namespace PromptVault.Core;

public enum BoardCommandId
{
    Undo,
    Redo,
    FocusSelection,
    FocusAll,
    ResetView,
    RemoveSelection,
    DeleteNote,
    LayerFront,
    LayerForward,
    LayerBackward,
    LayerBack,
    RotateLeft,
    RotateRight,
    ResetRotation,
    EnterCrop,
    CropHorizontal,
    CropVertical,
    ResetCrop,
    GroupSelection,
    UngroupSelection,
    RenameGroup,
    AddNote,
    CycleNoteColor,
    RelinkSource,
    OpenOriginal,
    Copy,
    Paste,
    NewBoard,
    RenameBoard,
    DeleteBoard,
    ChangeBackground,
    ShowInspector,
    ToggleTopmost,
    ShowTopBar,
    ShowSettings,
    ShowShortcuts
}

public enum BoardCommandContextKind
{
    Canvas,
    Item,
    MultiSelection,
    Note,
    Window
}

public enum BoardCommandDanger
{
    None,
    RemovesBoardContent,
    DeletesBoard
}

public sealed record BoardCommandDefinition(
    BoardCommandId Id,
    string Title,
    string? Shortcut,
    BoardCommandDanger Danger = BoardCommandDanger.None);

public readonly record struct BoardCommandState(
    BoardCommandContextKind Context,
    int SelectedItemCount,
    bool HasSelectedNote,
    bool CanUndo,
    bool CanRedo,
    bool HasManagedPaste,
    int BoardCount,
    bool SingleSourceMissing);

public static class BoardCommandPolicy
{
    public static bool CanExecute(BoardCommandId id, BoardCommandState state) => id switch
    {
        BoardCommandId.Undo => state.CanUndo,
        BoardCommandId.Redo => state.CanRedo,
        BoardCommandId.FocusSelection => state.SelectedItemCount > 0 || state.HasSelectedNote,
        BoardCommandId.RemoveSelection => state.SelectedItemCount > 0,
        BoardCommandId.DeleteNote or BoardCommandId.CycleNoteColor => state.HasSelectedNote,
        BoardCommandId.LayerFront or BoardCommandId.LayerForward
            or BoardCommandId.LayerBackward or BoardCommandId.LayerBack
            or BoardCommandId.RotateLeft or BoardCommandId.RotateRight
            or BoardCommandId.ResetRotation or BoardCommandId.CropHorizontal
            or BoardCommandId.CropVertical or BoardCommandId.ResetCrop
            or BoardCommandId.Copy => state.SelectedItemCount > 0,
        BoardCommandId.GroupSelection => state.SelectedItemCount > 1,
        BoardCommandId.UngroupSelection or BoardCommandId.RenameGroup => state.SelectedItemCount > 0,
        BoardCommandId.RelinkSource or BoardCommandId.OpenOriginal or BoardCommandId.EnterCrop => state.SelectedItemCount == 1,
        BoardCommandId.Paste => state.HasManagedPaste,
        BoardCommandId.RenameBoard => state.BoardCount > 0,
        BoardCommandId.DeleteBoard => state.BoardCount > 1,
        _ => true
    };
}

public enum BoardRightGestureKind
{
    PendingMenu,
    Drag,
    Menu
}

public sealed class BoardRightGestureClassifier
{
    public const double DefaultThreshold = 6;
    private readonly double _startX;
    private readonly double _startY;
    private readonly double _threshold;
    private bool _dragged;

    public BoardRightGestureClassifier(double startX, double startY, double threshold = DefaultThreshold)
    {
        _startX = startX;
        _startY = startY;
        _threshold = threshold;
    }

    public BoardRightGestureKind Move(double x, double y)
    {
        if (!_dragged && BoardInteractionEngine.ExceedsDragThreshold(_startX, _startY, x, y, _threshold))
            _dragged = true;
        return _dragged ? BoardRightGestureKind.Drag : BoardRightGestureKind.PendingMenu;
    }

    public BoardRightGestureKind Release(double x, double y) =>
        Move(x, y) == BoardRightGestureKind.Drag
            ? BoardRightGestureKind.Drag
            : BoardRightGestureKind.Menu;
}
