using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardTransformEngineTests
{
    [Fact]
    public void DefaultCornerResizePreservesAspectAndOppositeAnchor()
    {
        var result = BoardTransformEngine.ResizeBounds(
            new BoardWorldRect(10, 20, 200, 100), BoardResizeHandle.BottomRight, 100, 5, true, false);
        Assert.Equal(10, result.X, 6);
        Assert.Equal(20, result.Y, 6);
        Assert.Equal(300, result.Width, 6);
        Assert.Equal(150, result.Height, 6);
    }

    [Fact]
    public void ShiftFreeResizeChangesAxesIndependently()
    {
        var result = BoardTransformEngine.ResizeBounds(
            new BoardWorldRect(10, 20, 200, 100), BoardResizeHandle.BottomRight, 50, -20, false, false);
        Assert.Equal(250, result.Width, 6);
        Assert.Equal(80, result.Height, 6);
    }

    [Fact]
    public void AltResizeKeepsWorldCenter()
    {
        var original = new BoardWorldRect(10, 20, 200, 100);
        var result = BoardTransformEngine.ResizeBounds(original, BoardResizeHandle.TopLeft, -25, -10, true, true);
        Assert.Equal(original.X + original.Width / 2, result.X + result.Width / 2, 6);
        Assert.Equal(original.Y + original.Height / 2, result.Y + result.Height / 2, 6);
    }

    [Fact]
    public void MultiSelectionScalePreservesRelativeLayout()
    {
        var items = new[] { Item(1, 0, 0, 100, 100), Item(2, 200, 0, 100, 100) };
        var result = BoardTransformEngine.ScaleSelection(
            items, new HashSet<long> { 1, 2 }, new BoardWorldRect(0, 0, 300, 100), new BoardWorldRect(0, 0, 600, 200));
        Assert.Equal(200, result[0].Width, 6);
        Assert.Equal(400, result[1].X, 6);
        Assert.Equal(200, result[1].Width, 6);
    }

    [Fact]
    public void MultiSelectionRotationMovesCentersAndSnaps()
    {
        var items = new[] { Item(1, 0, 0, 100, 100), Item(2, 200, 0, 100, 100) };
        var result = BoardTransformEngine.RotateSelection(items, new HashSet<long> { 1, 2 }, 150, 50, 22, true);
        Assert.All(result, item => Assert.Equal(15, item.Rotation, 6));
        Assert.NotEqual(items[0].Y, result[0].Y);
    }

    [Fact]
    public void CropIsNonDestructiveAndClamped()
    {
        var item = Item(1, 0, 0, 100, 100);
        var cropped = BoardTransformEngine.CropFromCorner(item, BoardResizeHandle.TopLeft, 20, 10);
        Assert.Equal(0.2, cropped.CropLeft, 6);
        Assert.Equal(0.1, cropped.CropTop, 6);
        Assert.Equal(item.Width, cropped.Width);
        Assert.Equal(item.Height, cropped.Height);
    }

    private static BoardItemRecord Item(long id, double x, double y, double width, double height)
    {
        var now = DateTimeOffset.UtcNow;
        return new BoardItemRecord(id, 1, id, $"{id}.png", null, null, null, null, 100, 100, "PNG",
            x, y, width, height, 0, 0, 0, 0, 0, 0, null, now, now);
    }
}
