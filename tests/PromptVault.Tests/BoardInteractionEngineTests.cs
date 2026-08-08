using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardInteractionEngineTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void PlainRightDragAlwaysPansCanvasRegardlessOfWindowState(bool control, bool maximized, bool expected)
    {
        Assert.Equal(expected, BoardInteractionEngine.ShouldPanCanvasWithRightDrag(control, maximized));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void OnlyCtrlRightDragMovesWindowRegardlessOfWindowState(
        bool control, bool maximized, bool expected) =>
        Assert.Equal(expected, BoardInteractionEngine.ShouldMoveWindowWithRightDrag(control, maximized));

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void TransformThumbNeverStartsBlankCanvasGesture(
        bool pointerOriginatesFromThumb,
        bool pointerIsOverBlankCanvas,
        bool expected) =>
        Assert.Equal(expected, BoardInteractionEngine.ShouldBeginBlankCanvasGesture(
            pointerOriginatesFromThumb,
            pointerIsOverBlankCanvas));

    [Fact]
    public void ResolvePriorityUsesTheDocumentedInputLadder()
    {
        Assert.Equal(BoardInputPriority.Modal, BoardInteractionEngine.ResolvePriority(true, true, true, true, true));
        Assert.Equal(BoardInputPriority.TextEditing, BoardInteractionEngine.ResolvePriority(false, true, true, true, true));
        Assert.Equal(BoardInputPriority.Transform, BoardInteractionEngine.ResolvePriority(false, false, true, true, true));
        Assert.Equal(BoardInputPriority.Overlay, BoardInteractionEngine.ResolvePriority(false, false, false, true, true));
        Assert.Equal(BoardInputPriority.FocusNavigation, BoardInteractionEngine.ResolvePriority(false, false, false, false, true));
        Assert.Equal(BoardInputPriority.NormalCanvas, BoardInteractionEngine.ResolvePriority(false, false, false, false, false));
    }

    [Theory]
    [InlineData(0, 0, 3, 3, false)]
    [InlineData(0, 0, 5, 0, true)]
    [InlineData(1, 1, 4, 5, true)]
    public void DragThresholdIgnoresClickJitter(double sx, double sy, double x, double y, bool expected) =>
        Assert.Equal(expected, BoardInteractionEngine.ExceedsDragThreshold(sx, sy, x, y));

    [Fact]
    public void ShiftClickAddsAndRemovesWholeGroupsAtomically()
    {
        var items = new[] { Item(1, 10), Item(2, 10), Item(3, null) };
        var added = BoardInteractionEngine.SelectItem(items, new HashSet<long> { 3 }, 1, additive: true);
        Assert.Equal(new long[] { 1, 2, 3 }, added.Order());
        var removed = BoardInteractionEngine.SelectItem(items, added, 2, additive: true);
        Assert.Equal(new long[] { 3 }, removed.Order());
    }

    [Fact]
    public void ClickingAnAlreadySelectedItemPreservesTheDragSelection()
    {
        var items = new[] { Item(1, null), Item(2, null) };
        var result = BoardInteractionEngine.SelectItem(items, new HashSet<long> { 1, 2 }, 2, additive: false);
        Assert.Equal(new long[] { 1, 2 }, result.Order());
    }

    [Fact]
    public void MarqueeUsesRotatedBoundsInEitherDirectionAndExpandsGroups()
    {
        var items = new[]
        {
            Item(1, 8, x: 90, y: 90, width: 20, height: 100, rotation: 45),
            Item(2, 8, x: 500, y: 500),
            Item(3, null, x: 800, y: 800)
        };
        var forward = BoardInteractionEngine.SelectItemsInMarquee(
            items,
            new BoardWorldRect(50, 50, 80, 80),
            new HashSet<long>(),
            additive: false);
        var reverse = BoardInteractionEngine.SelectItemsInMarquee(
            items,
            new BoardWorldRect(130, 130, -80, -80),
            new HashSet<long>(),
            additive: false);
        Assert.Equal(new long[] { 1, 2 }, forward.Order());
        Assert.Equal(forward.Order(), reverse.Order());
    }

    [Fact]
    public void ShiftMarqueeAddsAndNotesUseRectangleIntersection()
    {
        var items = new[] { Item(1, null, x: 0, y: 0), Item(2, null, x: 200, y: 200) };
        var selected = BoardInteractionEngine.SelectItemsInMarquee(
            items,
            new BoardWorldRect(190, 190, 30, 30),
            new HashSet<long> { 1 },
            additive: true);
        var now = DateTimeOffset.UtcNow;
        var notes = new[] { new BoardNoteRecord(7, 1, "n", 205, 205, 50, 40, 0, "yellow", now, now) };
        Assert.Equal(new long[] { 1, 2 }, selected.Order());
        Assert.Equal(new long[] { 7 }, BoardInteractionEngine.SelectNotesInMarquee(notes, new BoardWorldRect(220, 220, -20, -20)));
    }

    private static BoardItemRecord Item(
        long id,
        long? groupId,
        double x = 0,
        double y = 0,
        double width = 100,
        double height = 100,
        double rotation = 0)
    {
        var now = DateTimeOffset.UtcNow;
        return new BoardItemRecord(
            id, 1, id, $"{id}.png", null, null, null, null,
            100, 100, "PNG", x, y, width, height, 0, rotation,
            0, 0, 0, 0, groupId, now, now);
    }
}
