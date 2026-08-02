using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardCommandSystemTests
{
    [Fact]
    public void CommandPolicyKeepsUnsafeAndInapplicableActionsDisabled()
    {
        var empty = new BoardCommandState(BoardCommandContextKind.Canvas, 0, false, false, false, false, 1, false);
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.RemoveSelection, empty));
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.DeleteBoard, empty));
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.Paste, empty));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.NewBoard, empty));

        var selected = empty with { SelectedItemCount = 2, CanUndo = true, BoardCount = 2 };
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.RemoveSelection, selected));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.GroupSelection, selected));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.DeleteBoard, selected));
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
