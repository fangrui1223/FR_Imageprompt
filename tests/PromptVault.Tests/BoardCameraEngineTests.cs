using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardCameraEngineTests
{
    [Theory]
    [InlineData(0, 10, 20, 100, 50)]
    [InlineData(90, 35, -5, 50, 100)]
    [InlineData(180, 10, 20, 100, 50)]
    public void RotatedBoundsHandlesOrthogonalAngles(
        double rotation,
        double x,
        double y,
        double width,
        double height)
    {
        var bounds = BoardCameraEngine.RotatedBounds(10, 20, 100, 50, rotation);

        Assert.Equal(x, bounds.X, 8);
        Assert.Equal(y, bounds.Y, 8);
        Assert.Equal(width, bounds.Width, 8);
        Assert.Equal(height, bounds.Height, 8);
    }

    [Theory]
    [InlineData(45, 106.06601717798213, 106.06601717798213)]
    [InlineData(30, 111.60254037844388, 93.30127018922192)]
    [InlineData(-17, 110.24906083244038, 77.0524082704254)]
    public void RotatedBoundsHandlesArbitraryAngles(
        double rotation,
        double expectedWidth,
        double expectedHeight)
    {
        var bounds = BoardCameraEngine.RotatedBounds(10, 20, 100, 50, rotation);

        Assert.Equal(expectedWidth, bounds.Width, 8);
        Assert.Equal(expectedHeight, bounds.Height, 8);
        Assert.Equal(60, bounds.X + bounds.Width / 2, 8);
        Assert.Equal(45, bounds.Y + bounds.Height / 2, 8);
    }

    [Fact]
    public void ContentBoundsUnitesRotatedImagesAndNotes()
    {
        var now = DateTimeOffset.UtcNow;
        var item = Item(1, 100, 100, 200, 100, 90, now);
        var note = Note(2, -50, 20, 80, 60, now);

        var result = BoardCameraEngine.ContentBounds([item], [note]);

        Assert.True(result.HasValue);
        Assert.Equal(-50, result.Bounds.X, 8);
        Assert.Equal(20, result.Bounds.Y, 8);
        Assert.Equal(300, result.Bounds.Width, 8);
        Assert.Equal(230, result.Bounds.Height, 8);
    }

    [Fact]
    public void SelectionBoundsIncludesOnlySelectedObjects()
    {
        var now = DateTimeOffset.UtcNow;
        var selected = Item(1, 100, 100, 200, 100, 0, now);
        var ignored = Item(2, 5000, 5000, 300, 200, 45, now);
        var note = Note(3, -100, -50, 40, 30, now);

        var result = BoardCameraEngine.SelectionBounds(
            [selected, ignored],
            new HashSet<long> { 1 },
            [note],
            3);

        Assert.True(result.HasValue);
        Assert.Equal(-100, result.Bounds.X, 8);
        Assert.Equal(-50, result.Bounds.Y, 8);
        Assert.Equal(400, result.Bounds.Width, 8);
        Assert.Equal(250, result.Bounds.Height, 8);
    }

    [Fact]
    public void EmptyContentAndSelectionReturnExplicitEmptyState()
    {
        Assert.False(BoardCameraEngine.ContentBounds([], []).HasValue);
        Assert.False(BoardCameraEngine.SelectionBounds(
            [],
            new HashSet<long>(),
            [],
            (long?)null).HasValue);
    }

    [Fact]
    public void FitBoundsUsesSixPercentPaddingWithinLimits()
    {
        var bounds = BoardBoundsResult.From(new BoardWorldRect(100, 200, 1000, 500));

        var fitted = BoardCameraEngine.FitBounds(bounds, 1920, 1080);

        Assert.Equal(1.7904, fitted.Zoom, 8);
        Assert.Equal(960 - 600 * fitted.Zoom, fitted.OffsetX, 8);
        Assert.Equal(540 - 450 * fitted.Zoom, fitted.OffsetY, 8);
    }

    [Theory]
    [InlineData(400, 300, 336, 236)]
    [InlineData(4000, 2000, 3808, 1808)]
    public void FitBoundsClampsPadding(
        double viewportWidth,
        double viewportHeight,
        double expectedAvailableWidth,
        double expectedAvailableHeight)
    {
        var fitted = BoardCameraEngine.FitBounds(
            BoardBoundsResult.From(new BoardWorldRect(0, 0, 1, 1)),
            viewportWidth,
            viewportHeight);

        var expectedZoom = Math.Min(
            BoardViewportEngine.MaximumZoom,
            Math.Min(expectedAvailableWidth, expectedAvailableHeight));
        Assert.Equal(expectedZoom, fitted.Zoom, 8);
    }

    [Fact]
    public void FitBoundsHonorsZoomLimitsForTinyAndExtremeContent()
    {
        var tiny = BoardCameraEngine.FitBounds(
            BoardBoundsResult.From(new BoardWorldRect(4, 7, 0.001, 0.001)),
            1600,
            900);
        var extreme = BoardCameraEngine.FitBounds(
            BoardBoundsResult.From(new BoardWorldRect(-1_000_000, -10, 2_000_000, 20)),
            1600,
            900);
        var extremeTall = BoardCameraEngine.FitBounds(
            BoardBoundsResult.From(new BoardWorldRect(-10, -1_000_000, 20, 2_000_000)),
            1600,
            900);

        Assert.Equal(BoardViewportEngine.MaximumZoom, tiny.Zoom);
        Assert.Equal(BoardViewportEngine.MinimumZoom, extreme.Zoom);
        Assert.Equal(BoardViewportEngine.MinimumZoom, extremeTall.Zoom);
    }

    [Fact]
    public void FitBoundsKeepsEveryCornerOfArbitrarilyRotatedImageVisible()
    {
        const double x = 300;
        const double y = 220;
        const double width = 640;
        const double height = 240;
        const double rotation = 37;
        var bounds = BoardCameraEngine.RotatedBounds(x, y, width, height, rotation);
        var fitted = BoardCameraEngine.FitBounds(
            BoardBoundsResult.From(bounds),
            1600,
            900);
        var radians = rotation * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var centerX = x + width / 2;
        var centerY = y + height / 2;
        var corners = new[]
        {
            (-width / 2, -height / 2),
            (width / 2, -height / 2),
            (width / 2, height / 2),
            (-width / 2, height / 2)
        };

        foreach (var (localX, localY) in corners)
        {
            var worldX = centerX + localX * cosine - localY * sine;
            var worldY = centerY + localX * sine + localY * cosine;
            var screenX = worldX * fitted.Zoom + fitted.OffsetX;
            var screenY = worldY * fitted.Zoom + fitted.OffsetY;
            Assert.InRange(screenX, 32, 1568);
            Assert.InRange(screenY, 32, 868);
        }
    }

    [Fact]
    public void FitBoundsCentersEmptyBoardAtOneHundredPercent()
    {
        var fitted = BoardCameraEngine.FitBounds(BoardBoundsResult.Empty, 1600, 900);

        Assert.Equal(new BoardViewport(800, 450, 1, 1600, 900), fitted);
    }

    [Fact]
    public void SnapshotRestoresExactWorldCenterAndZoomAfterResize()
    {
        var working = new BoardViewport(127.25, -63.5, 1.75, 1600, 900);
        var snapshot = BoardCameraSnapshot.Capture(working);
        var originalCenter = BoardViewportEngine.ScreenToWorld(working, 800, 450);

        var restored = snapshot.Restore(2100, 1200);
        var restoredCenter = BoardViewportEngine.ScreenToWorld(restored, 1050, 600);

        Assert.Equal(working.Zoom, restored.Zoom, 12);
        Assert.Equal(originalCenter.X, restoredCenter.X, 12);
        Assert.Equal(originalCenter.Y, restoredCenter.Y, 12);
    }

    [Fact]
    public void SpaceFocusThenSpaceRestoresWorkingCameraExactly()
    {
        var controller = new BoardFocusController();
        var working = new BoardViewport(120, -40, 1.25, 1600, 900);
        var target = BoardFocusTarget.SingleItem(12);
        var bounds = BoardBoundsResult.From(new BoardWorldRect(800, 500, 320, 180));

        var focus = controller.ToggleSpace(working, target, bounds);
        var restore = controller.ToggleSpace(focus.End, target, bounds);

        Assert.True(focus.End.Zoom > working.Zoom);
        Assert.True(controller.Transition?.IsRestore);
        Assert.Equal(working, restore.End);
        Assert.False(controller.IsActive);
        Assert.Equal(working, controller.WorkingViewport(restore.Start));
    }

    [Fact]
    public void DoubleClickSameTargetRestoresButDifferentTargetKeepsSnapshot()
    {
        var controller = new BoardFocusController();
        var working = new BoardViewport(-25, 80, 0.75, 1600, 900);
        var first = BoardFocusTarget.SingleItem(1);
        var second = BoardFocusTarget.SingleItem(2);
        var firstFocus = controller.FocusOrRestoreSameTarget(
            working,
            first,
            BoardBoundsResult.From(new BoardWorldRect(0, 0, 100, 100)));
        var secondFocus = controller.FocusOrRestoreSameTarget(
            firstFocus.End,
            second,
            BoardBoundsResult.From(new BoardWorldRect(900, 400, 100, 100)));
        var restore = controller.FocusOrRestoreSameTarget(
            secondFocus.End,
            second,
            BoardBoundsResult.From(new BoardWorldRect(900, 400, 100, 100)));

        Assert.Equal(working, restore.End);
        Assert.True(restore.IsRestore);
    }

    [Fact]
    public void ForceFullBoardFocusNeverOverwritesOriginalWorkingCamera()
    {
        var controller = new BoardFocusController();
        var working = new BoardViewport(330, -120, 1.4, 1600, 900);
        var itemFocus = controller.ForceFocus(
            working,
            BoardFocusTarget.SingleItem(1),
            BoardBoundsResult.From(new BoardWorldRect(200, 300, 100, 100)));
        var fullFocus = controller.ForceFocus(
            itemFocus.End,
            BoardFocusTarget.FullBoard(),
            BoardBoundsResult.From(new BoardWorldRect(-500, -500, 3000, 2000)));
        var restore = controller.ToggleSpace(
            fullFocus.End,
            BoardFocusTarget.FullBoard(),
            BoardBoundsResult.Empty);

        Assert.Equal(working, restore.End);
    }

    [Fact]
    public void ResizeDuringFocusRefitsTargetButPreservesWorkingWorldCenter()
    {
        var controller = new BoardFocusController();
        var working = new BoardViewport(110, -90, 1.3, 1600, 900);
        var target = BoardFocusTarget.SingleItem(7);
        var bounds = BoardBoundsResult.From(new BoardWorldRect(500, 400, 200, 400));
        var focused = controller.ForceFocus(working, target, bounds);
        var resizedCurrent = focused.End with { Width = 2100, Height = 1200 };

        var resized = controller.Refit(resizedCurrent, bounds);
        var restore = controller.ToggleSpace(resized.End, target, bounds);
        var workingCenter = BoardViewportEngine.ScreenToWorld(working, 800, 450);
        var restoredCenter = BoardViewportEngine.ScreenToWorld(restore.End, 1050, 600);

        Assert.Equal(working.Zoom, restore.End.Zoom, 12);
        Assert.Equal(workingCenter.X, restoredCenter.X, 12);
        Assert.Equal(workingCenter.Y, restoredCenter.Y, 12);
    }

    [Fact]
    public void FocusTransitionsAdvanceGenerationAndStartFromCurrentMatrix()
    {
        var controller = new BoardFocusController();
        var firstStart = new BoardViewport(0, 0, 1, 1000, 700);
        var first = controller.ForceFocus(
            firstStart,
            BoardFocusTarget.SingleItem(1),
            BoardBoundsResult.From(new BoardWorldRect(200, 100, 100, 100)));
        var interruptedMatrix = BoardCameraEngine.Interpolate(first.Start, first.End, 0.4);
        var second = controller.ForceFocus(
            interruptedMatrix,
            BoardFocusTarget.SingleItem(2),
            BoardBoundsResult.From(new BoardWorldRect(800, 500, 100, 100)));

        Assert.True(second.Generation > first.Generation);
        Assert.Equal(interruptedMatrix, second.Start);
    }

    [Fact]
    public void AdjacentItemUsesStableCreationOrderAndWraps()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var items = new[]
        {
            Item(30, 0, 0, 10, 10, 0, start.AddSeconds(2)),
            Item(20, 0, 0, 10, 10, 0, start.AddSeconds(1)),
            Item(10, 0, 0, 10, 10, 0, start.AddSeconds(1))
        };

        Assert.Equal(20, BoardCameraEngine.AdjacentItemId(items, 10, 1));
        Assert.Equal(30, BoardCameraEngine.AdjacentItemId(items, 20, 1));
        Assert.Equal(10, BoardCameraEngine.AdjacentItemId(items, 30, 1));
        Assert.Equal(30, BoardCameraEngine.AdjacentItemId(items, 10, -1));
    }

    [Fact]
    public void WorkingViewportUsesSnapshotUntilRestoreAnimationCompletes()
    {
        var controller = new BoardFocusController();
        var working = new BoardViewport(75, -50, 1.1, 1600, 900);
        var target = BoardFocusTarget.SingleItem(1);
        var focused = controller.ForceFocus(
            working,
            target,
            BoardBoundsResult.From(new BoardWorldRect(100, 200, 200, 100)));
        var restore = controller.ToggleSpace(focused.End, target, BoardBoundsResult.Empty);

        Assert.Equal(working, controller.WorkingViewport(restore.Start));
        controller.CompleteTransition(restore.Generation);
        Assert.Equal(restore.End, controller.WorkingViewport(restore.End));
    }

    [Fact]
    public void OneHundredPercentCentersPreferredBoundsOrCanvasOrigin()
    {
        var bounds = BoardBoundsResult.From(new BoardWorldRect(200, 100, 400, 300));

        var centered = BoardCameraEngine.AtOneHundredPercent(bounds, 1600, 900);
        var center = BoardViewportEngine.ScreenToWorld(centered, 800, 450);
        var empty = BoardCameraEngine.AtOneHundredPercent(BoardBoundsResult.Empty, 1600, 900);
        var emptyCenter = BoardViewportEngine.ScreenToWorld(empty, 800, 450);

        Assert.Equal(1, centered.Zoom);
        Assert.Equal(400, center.X, 10);
        Assert.Equal(250, center.Y, 10);
        Assert.Equal(0, emptyCenter.X, 10);
        Assert.Equal(0, emptyCenter.Y, 10);
    }

    [Fact]
    public void MixedTemporaryCommandsPreserveFirstSnapshotUntilExplicitRestore()
    {
        var controller = new BoardFocusController();
        var working = new BoardViewport(-120, 75, 0.85, 1600, 900);
        var item = controller.ForceFocus(
            working,
            BoardFocusTarget.SingleItem(10),
            BoardBoundsResult.From(new BoardWorldRect(100, 200, 200, 300)));
        var all = controller.ForceFocus(
            item.End,
            BoardFocusTarget.FullBoard(),
            BoardBoundsResult.From(new BoardWorldRect(-500, -400, 3000, 1800)));
        var selection = BoardFocusTarget.Selection([20], [31, 32]);
        var oneHundred = BoardCameraEngine.AtOneHundredPercent(
            BoardBoundsResult.From(new BoardWorldRect(600, 500, 500, 250)),
            1600,
            900);
        var reset = controller.ForceViewport(all.End, selection, oneHundred);

        var restore = controller.RestoreIfAvailable(reset.End);

        Assert.NotNull(restore);
        Assert.True(restore.Value.IsRestore);
        Assert.Equal(working.Zoom, restore.Value.End.Zoom, 12);
        Assert.Equal(working.OffsetX, restore.Value.End.OffsetX, 12);
        Assert.Equal(working.OffsetY, restore.Value.End.OffsetY, 12);
        Assert.Equal(working.Width, restore.Value.End.Width, 12);
        Assert.Equal(working.Height, restore.Value.End.Height, 12);
        Assert.Equal([31L, 32L], selection.NoteIds);
    }

    [Fact]
    public void RestoreWithoutTemporarySnapshotIsNoOpSignal()
    {
        var controller = new BoardFocusController();
        var working = new BoardViewport(10, 20, 1, 1200, 800);

        Assert.Null(controller.RestoreIfAvailable(working));
        Assert.False(controller.IsActive);
        Assert.Equal(working, controller.WorkingViewport(working));
    }

    private static BoardItemRecord Item(
        long id,
        double x,
        double y,
        double width,
        double height,
        double rotation,
        DateTimeOffset createdAt) => new(
        id,
        1,
        id,
        $"originals/{id}.png",
        null,
        $"originals/{id}.png",
        $"thumbnails/{id}.jpg",
        $"medium/{id}.jpg",
        1600,
        900,
        "png",
        x,
        y,
        width,
        height,
        (int)id,
        rotation,
        0,
        0,
        0,
        0,
        null,
        createdAt,
        createdAt);

    private static BoardNoteRecord Note(
        long id,
        double x,
        double y,
        double width,
        double height,
        DateTimeOffset createdAt) => new(
        id,
        1,
        "note",
        x,
        y,
        width,
        height,
        0,
        "yellow",
        createdAt,
        createdAt);
}
