using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardCropEngineTests
{
    [Theory]
    [InlineData(400, 100, 100, 100, 0.25, 1.0)]
    [InlineData(100, 400, 100, 100, 1.0, 0.25)]
    [InlineData(100, 100, 400, 100, 1.0, 0.25)]
    [InlineData(100, 100, 100, 400, 0.25, 1.0)]
    public void MinimumCoverMatchesSourceAndFrameAspect(
        double nw, double nh, double fw, double fh, double expectedWidth, double expectedHeight)
    {
        var result = BoardCropEngine.MinimumCover(nw, nh, fw, fh);
        Assert.True(result.IsValid);
        Assert.Equal(expectedWidth, result.Width, 6);
        Assert.Equal(expectedHeight, result.Height, 6);
        Assert.Equal((1 - expectedWidth) / 2, result.X, 6);
        Assert.Equal((1 - expectedHeight) / 2, result.Y, 6);
    }

    [Fact]
    public void PointerAnchorIsStableAcrossZoom()
    {
        var minimum = new BoardCropViewport(0, 0.25, 1, 0.5);
        var current = new BoardCropViewport(0.1, 0.3, 0.8, 0.4);
        var beforeX = current.X + current.Width * 0.8;
        var beforeY = current.Y + current.Height * 0.2;
        var result = BoardCropEngine.ZoomAt(current, minimum, 0.8, 0.2, 1.12);
        Assert.Equal(beforeX, result.X + result.Width * 0.8, 6);
        Assert.Equal(beforeY, result.Y + result.Height * 0.2, 6);
    }

    [Fact]
    public void ZoomIsClampedBetweenMinimumCoverAndEightTimes()
    {
        var minimum = new BoardCropViewport(0.25, 0, 0.5, 1);
        var zoomed = minimum;
        for (var i = 0; i < 100; i++) zoomed = BoardCropEngine.ZoomAt(zoomed, minimum, 0.5, 0.5, 1.12);
        Assert.Equal(minimum.Width / 8, zoomed.Width, 6);
        Assert.Equal(minimum.Height / 8, zoomed.Height, 6);
        for (var i = 0; i < 100; i++) zoomed = BoardCropEngine.ZoomAt(zoomed, minimum, 0.5, 0.5, 1 / 1.12);
        Assert.Equal(minimum, zoomed);
    }

    [Fact]
    public void PanNeverExposesOutsideSource()
    {
        var current = new BoardCropViewport(0.2, 0.3, 0.4, 0.2);
        var upperLeft = BoardCropEngine.Pan(current, 10000, 10000, 400, 200);
        var lowerRight = BoardCropEngine.Pan(current, -10000, -10000, 400, 200);
        Assert.Equal(0, upperLeft.X, 6);
        Assert.Equal(0, upperLeft.Y, 6);
        Assert.Equal(0.6, lowerRight.X, 6);
        Assert.Equal(0.8, lowerRight.Y, 6);
    }

    [Fact]
    public void FieldsRoundTripAndLegacyUniformFillBecomesEffectiveViewport()
    {
        var item = Item() with { CropLeft = 0.1, CropRight = 0.1, Width = 100, Height = 100 };
        var effective = BoardCropEngine.FromItem(item);
        var applied = BoardCropEngine.Apply(item, effective);
        Assert.True(effective.IsValid);
        Assert.Equal(effective, BoardCropEngine.FromItem(applied));
        Assert.Equal(0.25, effective.Width, 6);
    }

    private static BoardItemRecord Item()
    {
        var now = DateTimeOffset.UtcNow;
        return new BoardItemRecord(1, 1, 1, "1.png", null, null, null, null,
            400, 100, "PNG", 0, 0, 400, 100, 0, 0,
            0, 0, 0, 0, null, now, now);
    }
}
