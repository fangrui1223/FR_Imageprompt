using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardTransformEngineTests
{
    [Theory]
    [InlineData(BoardResizeHandle.TopLeft, -1, -1)]
    [InlineData(BoardResizeHandle.TopRight, 1, -1)]
    [InlineData(BoardResizeHandle.BottomLeft, -1, 1)]
    [InlineData(BoardResizeHandle.BottomRight, 1, 1)]
    public void ProportionalCornerResizeUsesContinuousDiagonalProjection(
        BoardResizeHandle handle,
        double directionX,
        double directionY)
    {
        var original = new BoardWorldRect(40, 60, 360, 640);
        var previous = original.Width;
        for (var step = 1; step <= 240; step++)
        {
            var distance = step * 1.25;
            var noise = Math.Sin(step * 0.37) * 0.45;
            var result = BoardTransformEngine.ResizeBounds(
                original,
                handle,
                directionX * (distance + noise),
                directionY * (distance - noise),
                preserveAspect: true,
                fromCenter: false);

            Assert.True(result.Width >= previous - 0.000001, $"step {step} regressed");
            Assert.Equal(original.Width / original.Height, result.Width / result.Height, 10);
            previous = result.Width;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProportionalProjectionDoesNotJumpAtFormerDominantAxisBoundary(bool fromCenter)
    {
        var original = new BoardWorldRect(0, 0, 320, 560);
        var before = BoardTransformEngine.ResizeBounds(
            original,
            BoardResizeHandle.BottomRight,
            79.999,
            139.998,
            preserveAspect: true,
            fromCenter: fromCenter);
        var after = BoardTransformEngine.ResizeBounds(
            original,
            BoardResizeHandle.BottomRight,
            80.001,
            140.002,
            preserveAspect: true,
            fromCenter: fromCenter);

        Assert.InRange(Math.Abs(after.Width - before.Width), 0, 0.02);
        Assert.InRange(Math.Abs(after.Height - before.Height), 0, 0.04);
    }

    [Fact]
    public void FreeResizeRetainsIndependentAxisBehavior()
    {
        var original = new BoardWorldRect(10, 20, 300, 500);
        var result = BoardTransformEngine.ResizeBounds(
            original,
            BoardResizeHandle.BottomRight,
            120,
            15,
            preserveAspect: false,
            fromCenter: false);

        Assert.Equal(420, result.Width, 6);
        Assert.Equal(515, result.Height, 6);
    }
    [Fact]
    public void DefaultCornerResizePreservesAspectAndOppositeAnchor()
    {
        var result = BoardTransformEngine.ResizeBounds(
            new BoardWorldRect(10, 20, 200, 100), BoardResizeHandle.BottomRight, 100, 5, true, false);
        Assert.Equal(10, result.X, 6);
        Assert.Equal(20, result.Y, 6);
        Assert.Equal(282, result.Width, 6);
        Assert.Equal(141, result.Height, 6);
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
    public void EdgeResizeChangesOnlyItsAxis()
    {
        var right = BoardTransformEngine.ResizeBounds(
            new BoardWorldRect(10, 20, 200, 100), BoardResizeHandle.Right, 50, 80, false, false);
        var top = BoardTransformEngine.ResizeBounds(
            new BoardWorldRect(10, 20, 200, 100), BoardResizeHandle.Top, 80, 25, false, false);

        Assert.Equal(new BoardWorldRect(10, 20, 250, 100), right);
        Assert.Equal(new BoardWorldRect(10, 45, 200, 75), top);
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
    public void ResizeGestureAlwaysRecalculatesFromInitialSnapshot()
    {
        var gesture = new BoardResizeGesture(
            new BoardWorldRect(10, 20, 200, 100),
            BoardResizeHandle.BottomRight,
            100,
            100);
        var proportional = gesture.Calculate(150, 120, preserveAspect: true, fromCenter: false);
        var free = gesture.Calculate(150, 120, preserveAspect: false, fromCenter: false);
        Assert.Equal(248, proportional.Width, 6);
        Assert.Equal(124, proportional.Height, 6);
        Assert.Equal(250, free.Width, 6);
        Assert.Equal(120, free.Height, 6);
    }

    [Fact]
    public void NoteMinimumWidthAndHeightAreIndependent()
    {
        var result = BoardTransformEngine.ResizeBounds(
            new BoardWorldRect(10, 20, 300, 220),
            BoardResizeHandle.TopLeft,
            500,
            500,
            preserveAspect: false,
            fromCenter: false,
            minimumEdge: 120,
            minimumHeight: 100);
        Assert.Equal(new BoardWorldRect(190, 140, 120, 100), result);
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
    public void ScaleItemMatchesBatchSelectionScale()
    {
        var items = new[] { Item(1, 20, 30, 600, 900), Item(2, 700, 80, 240, 180) };
        var originalBounds = new BoardWorldRect(20, 30, 920, 900);
        var targetBounds = new BoardWorldRect(-80, -70, 1380, 1350);
        var batch = BoardTransformEngine.ScaleSelection(
            items, new HashSet<long> { 1, 2 }, originalBounds, targetBounds);

        Assert.Equal(batch[0], BoardTransformEngine.ScaleItem(items[0], originalBounds, targetBounds));
        Assert.Equal(batch[1], BoardTransformEngine.ScaleItem(items[1], originalBounds, targetBounds));
    }

    [Fact]
    public void AbsolutePointerDeltasProduceMonotonicLargeImageResize()
    {
        var original = new BoardWorldRect(20, 30, 600, 900);
        var gesture = new BoardResizeGesture(original, BoardResizeHandle.BottomRight, 0, 0);
        var widths = Enumerable.Range(1, 12)
            .Select(step => BoardTransformEngine.ScreenDeltaToLocal(step * 18, step * 27, 1.5, 0))
            .Select(delta => gesture.Calculate(delta.X, delta.Y, preserveAspect: true, fromCenter: false).Width)
            .ToArray();

        Assert.All(widths.Zip(widths.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    [Theory]
    [InlineData(0, 90, 60, 0)]
    [InlineData(90, 90, 0, -60)]
    [InlineData(180, 90, -60, 0)]
    public void ScreenDeltaConvertsToRotatedImageLocalCoordinates(
        double rotation,
        double screenX,
        double expectedX,
        double expectedY)
    {
        var actual = BoardTransformEngine.ScreenDeltaToLocal(screenX, 0, 1.5, rotation);
        Assert.Equal(expectedX, actual.X, 6);
        Assert.Equal(expectedY, actual.Y, 6);
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

    [Theory]
    [InlineData(400, 100, 320, 80)]
    [InlineData(100, 400, 80, 320)]
    [InlineData(100, 100, 320, 320)]
    public void ResetSizePreservesNaturalAspectAndWorldCenter(
        int naturalWidth,
        int naturalHeight,
        double expectedWidth,
        double expectedHeight)
    {
        var original = Item(1, 100, 200, 700, 500) with
        {
            NaturalWidth = naturalWidth,
            NaturalHeight = naturalHeight
        };

        var result = BoardTransformEngine.ResetSize(original);

        Assert.Equal(expectedWidth, result.Width, 6);
        Assert.Equal(expectedHeight, result.Height, 6);
        Assert.Equal(original.X + original.Width / 2, result.X + result.Width / 2, 6);
        Assert.Equal(original.Y + original.Height / 2, result.Y + result.Height / 2, 6);
    }

    private static BoardItemRecord Item(long id, double x, double y, double width, double height)
    {
        var now = DateTimeOffset.UtcNow;
        return new BoardItemRecord(id, 1, id, $"{id}.png", null, null, null, null, 100, 100, "PNG",
            x, y, width, height, 0, 0, 0, 0, 0, 0, null, now, now);
    }
}
