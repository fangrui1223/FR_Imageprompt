using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class CaptureStateMachineTests
{
    [Theory]
    [InlineData(CaptureState.ImageDetected, CaptureState.PreparingImage)]
    [InlineData(CaptureState.PreparingImage, CaptureState.WaitingForPrompt)]
    [InlineData(CaptureState.WaitingForPrompt, CaptureState.PromptDebouncing)]
    [InlineData(CaptureState.PromptDebouncing, CaptureState.Saved)]
    [InlineData(CaptureState.Saved, CaptureState.WaitingForAi)]
    [InlineData(CaptureState.Saved, CaptureState.Undone)]
    [InlineData(CaptureState.NeedsPrompt, CaptureState.PromptDebouncing)]
    [InlineData(CaptureState.Failed, CaptureState.PreparingImage)]
    public void AllowsExpectedTransitions(CaptureState from, CaptureState to)
    {
        Assert.True(CaptureStateMachine.CanTransition(from, to));
        CaptureStateMachine.EnsureTransition(from, to);
    }

    [Theory]
    [InlineData(CaptureState.ImageDetected, CaptureState.Saved)]
    [InlineData(CaptureState.WaitingForPrompt, CaptureState.Saved)]
    [InlineData(CaptureState.Saved, CaptureState.PromptDebouncing)]
    [InlineData(CaptureState.Undone, CaptureState.PreparingImage)]
    public void RejectsInvalidTransitions(CaptureState from, CaptureState to)
    {
        Assert.False(CaptureStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() =>
            CaptureStateMachine.EnsureTransition(from, to));
    }

    [Fact]
    public void ClassifiesRecoverableAndPromptWaitingStates()
    {
        Assert.True(CaptureStateMachine.IsRecoverable(CaptureState.PreparingImage));
        Assert.True(CaptureStateMachine.IsRecoverable(CaptureState.NeedsPrompt));
        Assert.False(CaptureStateMachine.IsRecoverable(CaptureState.Saved));
        Assert.True(CaptureStateMachine.IsAwaitingPrompt(CaptureState.PromptDebouncing));
        Assert.False(CaptureStateMachine.IsAwaitingPrompt(CaptureState.Failed));
    }
}
