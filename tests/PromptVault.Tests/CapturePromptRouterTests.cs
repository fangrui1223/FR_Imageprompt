using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class CapturePromptRouterTests
{
    [Fact]
    public void ConsecutiveImagesRouteTextToNewestPreparedCapture()
    {
        var now = DateTimeOffset.UtcNow;
        var older = Session(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CaptureState.WaitingForPrompt,
            now.AddSeconds(-2),
            now.AddMinutes(2));
        var newer = Session(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            CaptureState.WaitingForPrompt,
            now.AddSeconds(-1),
            now.AddMinutes(2));

        var selected = CapturePromptRouter.SelectLatestWaiting(
            [older, newer],
            now,
            _ => true);

        Assert.Equal(newer.Id, selected!.Id);
    }

    [Fact]
    public void RouterSkipsExpiredUnpreparedAndTerminalSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = Session(
            Guid.NewGuid(),
            CaptureState.WaitingForPrompt,
            now.AddSeconds(-4),
            now.AddMilliseconds(-1));
        var unprepared = Session(
            Guid.NewGuid(),
            CaptureState.WaitingForPrompt,
            now.AddSeconds(-3),
            now.AddMinutes(2));
        var needsPrompt = Session(
            Guid.NewGuid(),
            CaptureState.NeedsPrompt,
            now.AddSeconds(-2),
            now.AddMinutes(2));
        var eligible = Session(
            Guid.NewGuid(),
            CaptureState.PromptDebouncing,
            now.AddSeconds(-1),
            now.AddMinutes(2));

        var selected = CapturePromptRouter.SelectLatestWaiting(
            [expired, unprepared, needsPrompt, eligible],
            now,
            id => id != unprepared.Id);

        Assert.Equal(eligible.Id, selected!.Id);
    }

    private static CaptureSessionRecord Session(
        Guid id,
        CaptureState state,
        DateTimeOffset capturedAt,
        DateTimeOffset deadline) =>
        new(
            id,
            state,
            $".staging/{id:N}.png",
            $".staging/{id:N}.small.jpg",
            $".staging/{id:N}.medium.jpg",
            $"hash-{id:N}",
            "png",
            "png",
            640,
            480,
            "",
            "",
            null,
            "",
            capturedAt,
            capturedAt,
            deadline,
            null,
            false,
            null,
            null,
            null,
            null);
}
