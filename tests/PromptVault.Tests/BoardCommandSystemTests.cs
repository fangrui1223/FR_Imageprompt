using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardCommandSystemTests
{
    [Fact]
    public void CommandPolicyKeepsUnsafeAndInapplicableActionsDisabled()
    {
        var empty = new BoardCommandState(BoardCommandContextKind.Canvas, 0, 0, false, false, false, 1, false);
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.RemoveSelection, empty));
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.DeleteBoard, empty));
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.Paste, empty));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.NewBoard, empty));

        var selected = empty with { SelectedItemCount = 2, CanUndo = true, BoardCount = 2 };
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.RemoveSelection, selected));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.GroupSelection, selected));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.ResetSize, selected));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.DeleteBoard, selected));

        var note = empty with { SelectedNoteCount = 1 };
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.ResetSize, note));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.EditNote, note));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.FitNoteContent, note));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.ApplyNotePreset1, note));

        var notes = empty with { SelectedNoteCount = 3 };
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.EditNote, notes));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.ApplyNotePreset5, notes));
    }

    [Fact]
    public void RightGestureShortReleaseOpensMenuWithoutDrag()
    {
        var gesture = new BoardRightGestureClassifier(10, 20);
        Assert.Equal(BoardRightGestureKind.PendingMenu, gesture.Move(13, 23));
        Assert.Equal(BoardRightGestureKind.Menu, gesture.Release(14, 22));
    }

    [Fact]
    public void RightGestureCrossingSixDipSuppressesMenuPermanently()
    {
        var gesture = new BoardRightGestureClassifier(0, 0);
        Assert.Equal(BoardRightGestureKind.Drag, gesture.Move(6, 0));
        Assert.Equal(BoardRightGestureKind.Drag, gesture.Release(1, 1));
    }
}
