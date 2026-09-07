using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardManualCameraHistoryTests
{
    [Fact]
    public void MostRecentGestureTogglesBeforeAndAfterExactly()
    {
        var history = new BoardManualCameraHistory();
        var before = new BoardViewport(10, 20, 0.75, 1000, 700);
        var after = new BoardViewport(-320, 96, 1.35, 1000, 700);

        history.Record(before, after);

        Assert.Equal(before, history.Toggle(1000, 700));
        Assert.Equal(after, history.Toggle(1000, 700));
        Assert.Equal(before, history.Toggle(1000, 700));
    }

    [Fact]
    public void NewGestureOverwritesTheSingleHistoryLayer()
    {
        var history = new BoardManualCameraHistory();
        history.Record(
            new BoardViewport(0, 0, 1, 800, 600),
            new BoardViewport(20, 30, 1.1, 800, 600));
        var latestBefore = new BoardViewport(50, 60, 1.2, 800, 600);
        var latestAfter = new BoardViewport(70, 90, 1.4, 800, 600);

        history.Record(latestBefore, latestAfter);

        Assert.Equal(latestBefore, history.Toggle(800, 600));
        Assert.Equal(latestAfter, history.Toggle(800, 600));
    }

    [Fact]
    public void NoMovementDoesNotCreateHistory()
    {
        var history = new BoardManualCameraHistory();
        var view = new BoardViewport(0, 0, 1, 800, 600);

        history.Record(view, view);

        Assert.False(history.CanToggle);
        Assert.Null(history.Toggle(800, 600));
    }
}
