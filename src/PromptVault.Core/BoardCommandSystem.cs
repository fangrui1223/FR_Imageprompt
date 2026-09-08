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
    ResetSize,
    EnterCrop,
    CompleteCrop,
    CancelCrop,
    ResetCrop,
    GroupSelection,
    UngroupSelection,
    RenameGroup,
    AddNote,
    EditNote,
    FitNoteContent,
    ToggleNoteBackground,
    AlignNoteLeft,
    AlignNoteCenter,
    AlignNoteRight,
    ApplyNotePreset1,
    ApplyNotePreset2,
    ApplyNotePreset3,
    ApplyNotePreset4,
    ApplyNotePreset5,
    DuplicateNote,
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
    ShowShortcuts,
    SelectAll,
    ToggleReferenceLock,
    AlignImagesLeft,
    AlignImagesCenter,
    AlignImagesRight,
    AlignImagesTop,
    AlignImagesMiddle,
    AlignImagesBottom,
    DistributeImagesHorizontally,
    DistributeImagesVertically
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
    int SelectedNoteCount,
    bool CanUndo,
    bool CanRedo,
    bool HasManagedPaste,
    int BoardCount,
    bool SingleSourceMissing,
    bool ReferenceLocked = false)
{
    public bool HasSelectedNote => SelectedNoteCount > 0;
}

public static class BoardCommandPolicy
{
    public static bool IsAllowedWhenReferenceLocked(BoardCommandId id) => id is
        BoardCommandId.FocusSelection or BoardCommandId.FocusAll or BoardCommandId.ResetView
        or BoardCommandId.SelectAll or BoardCommandId.Copy or BoardCommandId.OpenOriginal
        or BoardCommandId.NewBoard or BoardCommandId.ShowInspector or BoardCommandId.ToggleTopmost
        or BoardCommandId.ShowTopBar or BoardCommandId.ShowSettings or BoardCommandId.ShowShortcuts
        or BoardCommandId.ToggleReferenceLock;

    public static bool CanExecute(BoardCommandId id, BoardCommandState state) =>
        (!state.ReferenceLocked || IsAllowedWhenReferenceLocked(id))
        && (!(state.SelectedItemCount > 0 && state.HasSelectedNote) || id is not (
            BoardCommandId.ResetSize or BoardCommandId.RotateLeft or BoardCommandId.RotateRight
            or BoardCommandId.ResetRotation or BoardCommandId.EnterCrop or BoardCommandId.ResetCrop
            or BoardCommandId.LayerFront or BoardCommandId.LayerForward or BoardCommandId.LayerBackward or BoardCommandId.LayerBack))
        && (id switch
    {
        BoardCommandId.Undo => state.CanUndo,
        BoardCommandId.Redo => state.CanRedo,
        BoardCommandId.FocusSelection => state.SelectedItemCount > 0 || state.HasSelectedNote,
        BoardCommandId.RemoveSelection => state.SelectedItemCount > 0 || state.HasSelectedNote,
        BoardCommandId.AlignImagesLeft or BoardCommandId.AlignImagesCenter or BoardCommandId.AlignImagesRight
            or BoardCommandId.AlignImagesTop or BoardCommandId.AlignImagesMiddle or BoardCommandId.AlignImagesBottom => state.SelectedItemCount >= 2,
        BoardCommandId.DistributeImagesHorizontally or BoardCommandId.DistributeImagesVertically => state.SelectedItemCount >= 3,
        BoardCommandId.DeleteNote or BoardCommandId.ToggleNoteBackground
            or BoardCommandId.AlignNoteLeft or BoardCommandId.AlignNoteCenter
            or BoardCommandId.AlignNoteRight or BoardCommandId.ApplyNotePreset1
            or BoardCommandId.ApplyNotePreset2 or BoardCommandId.ApplyNotePreset3
            or BoardCommandId.ApplyNotePreset4 or BoardCommandId.ApplyNotePreset5
            or BoardCommandId.DuplicateNote => state.HasSelectedNote,
        BoardCommandId.EditNote or BoardCommandId.FitNoteContent => state.SelectedNoteCount == 1,
        BoardCommandId.ResetSize => state.SelectedItemCount > 0 || state.HasSelectedNote,
        BoardCommandId.LayerFront or BoardCommandId.LayerForward
            or BoardCommandId.LayerBackward or BoardCommandId.LayerBack =>
            state.SelectedItemCount > 0 || state.HasSelectedNote,
        BoardCommandId.RotateLeft or BoardCommandId.RotateRight
            or BoardCommandId.ResetRotation or BoardCommandId.ResetCrop
            or BoardCommandId.Copy => state.SelectedItemCount > 0,
        BoardCommandId.CompleteCrop or BoardCommandId.CancelCrop => state.SelectedItemCount == 1,
        BoardCommandId.GroupSelection => state.SelectedItemCount > 1,
        BoardCommandId.UngroupSelection or BoardCommandId.RenameGroup => state.SelectedItemCount > 0,
        BoardCommandId.RelinkSource or BoardCommandId.OpenOriginal or BoardCommandId.EnterCrop => state.SelectedItemCount == 1,
        BoardCommandId.Paste => state.HasManagedPaste,
        BoardCommandId.RenameBoard => state.BoardCount > 0,
        BoardCommandId.DeleteBoard => state.BoardCount > 1,
        _ => true
    });
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
