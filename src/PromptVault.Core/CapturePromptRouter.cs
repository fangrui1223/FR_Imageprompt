namespace PromptVault.Core;

public static class CapturePromptRouter
{
    public static CaptureSessionRecord? SelectLatestWaiting(
        IEnumerable<CaptureSessionRecord> sessions,
        DateTimeOffset now,
        Func<Guid, bool> hasPreparedCapture) =>
        sessions
            .Where(session =>
                session.State is CaptureState.WaitingForPrompt or CaptureState.PromptDebouncing
                && session.PromptDeadlineAt > now
                && hasPreparedCapture(session.Id))
            .OrderByDescending(session => session.CapturedAt)
            .ThenByDescending(session => session.Id)
            .FirstOrDefault();
}
