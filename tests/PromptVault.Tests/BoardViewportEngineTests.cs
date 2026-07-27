using System.Diagnostics;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardViewportEngineTests
{
    [Fact]
    public void ZoomAtKeepsWorldPointUnderPointer()
    {
        var viewport = new BoardViewport(120, -40, 1, 1600, 900);
        var worldBefore = BoardViewportEngine.ScreenToWorld(viewport, 700, 450);

        var zoomed = BoardViewportEngine.ZoomAt(viewport, 700, 450, 2.5);
        var worldAfter = BoardViewportEngine.ScreenToWorld(zoomed, 700, 450);

        Assert.Equal(worldBefore.X, worldAfter.X, 8);
        Assert.Equal(worldBefore.Y, worldAfter.Y, 8);
    }

    [Fact]
    public void QueryVisibleCullsDistantItemsAndIncludesOverscan()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            Item(1, 10, 10, now),
            Item(2, 1150, 200, now),
            Item(3, 4000, 4000, now)
        };
        var visible = BoardViewportEngine.QueryVisible(
            items,
            new BoardViewport(0, 0, 1, 1000, 700),
            200);

        Assert.Equal([1L, 2L], visible.Select(item => item.Id));
    }

    [Fact]
    public void QueryVisibleCullsBoardNotesWithTheSameViewportRules()
    {
        var now = DateTimeOffset.UtcNow;
        var notes = new[]
        {
            new BoardNoteRecord(1, 4, "visible", 50, 80, 300, 200, 0, "yellow", now, now),
            new BoardNoteRecord(2, 4, "overscan", 1080, 80, 260, 180, 1, "blue", now, now),
            new BoardNoteRecord(3, 4, "distant", 5000, 5000, 300, 200, 2, "rose", now, now)
        };

        var visible = BoardViewportEngine.QueryVisible(
            notes,
            new BoardViewport(0, 0, 1, 1000, 700),
            200);

        Assert.Equal([1L, 2L], visible.Select(note => note.Id));
    }

    [Fact]
    public void FiveThousandItemVisibilityQueriesStayWithinInteractiveBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, 5000)
            .Select(index => Item(index + 1, index % 100 * 360, index / 100 * 260, now))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();
        var visibleCount = 0;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            visibleCount += BoardViewportEngine.QueryVisible(
                items,
                new BoardViewport(-iteration * 12, -iteration * 5, 1.3, 2560, 1707)).Count;
        }
        stopwatch.Stop();

        Assert.True(visibleCount > 0);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 500,
            $"200 次 5,000 项可见性查询耗时 {stopwatch.ElapsedMilliseconds}ms。");
    }

    private static BoardItemRecord Item(long id, double x, double y, DateTimeOffset now) => new(
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
        320,
        180,
        (int)id,
        0,
        0,
        0,
        0,
        0,
        null,
        now,
        now);
}
