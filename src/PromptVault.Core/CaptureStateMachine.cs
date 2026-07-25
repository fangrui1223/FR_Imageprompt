namespace PromptVault.Core;

public static class CaptureStateMachine
{
    private static readonly IReadOnlyDictionary<CaptureState, HashSet<CaptureState>> AllowedTransitions =
        new Dictionary<CaptureState, HashSet<CaptureState>>
        {
            [CaptureState.ImageDetected] =
            [
                CaptureState.PreparingImage,
                CaptureState.Failed,
                CaptureState.Undone
            ],
            [CaptureState.PreparingImage] =
            [
                CaptureState.WaitingForPrompt,
                CaptureState.NeedsPrompt,
                CaptureState.Failed,
                CaptureState.Undone
            ],
            [CaptureState.WaitingForPrompt] =
            [
                CaptureState.PromptDebouncing,
                CaptureState.NeedsPrompt,
                CaptureState.Failed,
                CaptureState.Undone
            ],
            [CaptureState.PromptDebouncing] =
            [
                CaptureState.PromptDebouncing,
                CaptureState.Saved,
                CaptureState.NeedsPrompt,
                CaptureState.Failed,
                CaptureState.Undone
            ],
            [CaptureState.Saved] =
            [
                CaptureState.WaitingForAi,
                CaptureState.Undone
            ],
            [CaptureState.WaitingForAi] =
            [
                CaptureState.Saved,
                CaptureState.Undone
            ],
            [CaptureState.NeedsPrompt] =
            [
                CaptureState.PromptDebouncing,
                CaptureState.Saved,
                CaptureState.Failed,
                CaptureState.Undone
            ],
            [CaptureState.Failed] =
            [
                CaptureState.PreparingImage,
                CaptureState.PromptDebouncing,
                CaptureState.Saved,
                CaptureState.NeedsPrompt,
                CaptureState.Undone
            ],
            [CaptureState.Undone] = []
        };

    public static bool CanTransition(CaptureState from, CaptureState to) =>
        AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureTransition(CaptureState from, CaptureState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"捕获状态不能从 {from} 变为 {to}。");
        }
    }

    public static bool IsRecoverable(CaptureState state) =>
        state is CaptureState.ImageDetected
            or CaptureState.PreparingImage
            or CaptureState.WaitingForPrompt
            or CaptureState.PromptDebouncing
            or CaptureState.NeedsPrompt
            or CaptureState.Failed;

    public static bool IsAwaitingPrompt(CaptureState state) =>
        state is CaptureState.WaitingForPrompt
            or CaptureState.PromptDebouncing
            or CaptureState.NeedsPrompt;
}
