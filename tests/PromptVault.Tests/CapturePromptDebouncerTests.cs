using System.Collections.Concurrent;
using System.Threading.Channels;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class CapturePromptDebouncerTests
{
    [Fact]
    public async Task NewPromptResetsDelayAndOnlyLatestValueCompletes()
    {
        var completed = new ConcurrentQueue<string>();
        var callbackCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = Channel.CreateUnbounded<TaskCompletionSource>();
        using var debouncer = new CapturePromptDebouncer((_, prompt, _) =>
        {
            completed.Enqueue(prompt);
            callbackCompleted.TrySetResult();
            return Task.CompletedTask;
        }, (_, token) =>
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            Assert.True(delays.Writer.TryWrite(completion));
            return completion.Task;
        });
        var captureId = Guid.NewGuid();

        debouncer.Submit(captureId, "partial", TimeSpan.FromMilliseconds(100));
        var firstDelay = await delays.Reader.ReadAsync();
        debouncer.Submit(captureId, "complete prompt", TimeSpan.FromMilliseconds(100));
        var secondDelay = await delays.Reader.ReadAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstDelay.Task);
        Assert.Empty(completed);

        secondDelay.TrySetResult();
        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["complete prompt"], completed.ToArray());
    }

    [Fact]
    public async Task ThousandMixedUpdatesProduceOneStableValuePerCapture()
    {
        const int captureCount = 25;
        var completed = new ConcurrentDictionary<Guid, ConcurrentQueue<string>>();
        var allCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var debouncer = new CapturePromptDebouncer((captureId, prompt, _) =>
        {
            completed.GetOrAdd(captureId, _ => new ConcurrentQueue<string>()).Enqueue(prompt);
            if (completed.Count == captureCount) allCompleted.TrySetResult();
            return Task.CompletedTask;
        });
        var ids = Enumerable.Range(0, captureCount).Select(_ => Guid.NewGuid()).ToArray();

        for (var index = 0; index < 1000; index++)
        {
            var captureId = ids[index % ids.Length];
            debouncer.Submit(
                captureId,
                $"prompt-{index}",
                TimeSpan.FromMilliseconds(150));
        }
        await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(captureCount, completed.Count);
        for (var idIndex = 0; idIndex < ids.Length; idIndex++)
        {
            var values = Assert.Single(completed[ids[idIndex]]);
            var lastIndex = 975 + idIndex;
            Assert.Equal($"prompt-{lastIndex}", values);
        }
    }

    [Fact]
    public async Task CancelAndDisposePreventCompletion()
    {
        var completed = 0;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var debouncer = new CapturePromptDebouncer((_, _, _) =>
        {
            Interlocked.Increment(ref completed);
            return Task.CompletedTask;
        });

        debouncer.Submit(first, "cancelled", TimeSpan.FromMilliseconds(80));
        debouncer.Cancel(first);
        debouncer.Submit(second, "disposed", TimeSpan.FromMilliseconds(80));
        debouncer.Dispose();
        await Task.Delay(130);

        Assert.Equal(0, Volatile.Read(ref completed));
    }

    [Fact]
    public void DefaultDelayIsSixHundredMilliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(600), CapturePromptDebouncer.DefaultDelay);
    }
}
