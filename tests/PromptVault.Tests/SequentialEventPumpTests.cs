using System.Collections.Concurrent;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class SequentialEventPumpTests
{
    [Fact]
    public async Task ProcessesOneThousandMixedEventsInOrderWithoutDuplicates()
    {
        const int count = 1000;
        var received = new ConcurrentQueue<TestEvent>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new SequentialEventPump<TestEvent>(async (item, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            received.Enqueue(item);
            if (received.Count == count) completed.TrySetResult();
        });

        for (var index = 0; index < count; index++)
        {
            Assert.True(pump.TryEnqueue(new TestEvent(index, index % 2 == 0 ? "image" : "text")));
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pump.Stop();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            Enumerable.Range(0, count),
            received.Select(item => item.Sequence));
        Assert.Equal(count, received.Select(item => item.Sequence).Distinct().Count());
        Assert.Equal(count / 2, received.Count(item => item.Kind == "image"));
        Assert.Equal(count / 2, received.Count(item => item.Kind == "text"));
    }

    [Fact]
    public async Task HandlerFailureDoesNotStopLaterEvents()
    {
        var received = new ConcurrentQueue<int>();
        var errors = new ConcurrentQueue<Exception>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new SequentialEventPump<int>(
            (item, _) =>
            {
                if (item == 2) throw new InvalidOperationException("expected");
                received.Enqueue(item);
                if (item == 3) completed.TrySetResult();
                return Task.CompletedTask;
            },
            errors.Enqueue);

        Assert.True(pump.TryEnqueue(1));
        Assert.True(pump.TryEnqueue(2));
        Assert.True(pump.TryEnqueue(3));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pump.Stop();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([1, 3], received);
        Assert.Single(errors);
    }

    [Fact]
    public async Task TextQueuedDuringImageProcessingRunsAfterImageIsReady()
    {
        var imageReady = false;
        string? appliedPrompt = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new SequentialEventPump<TestEvent>(async (item, cancellationToken) =>
        {
            if (item.Kind == "image")
            {
                await Task.Delay(100, cancellationToken);
                imageReady = true;
                return;
            }

            Assert.True(imageReady);
            appliedPrompt = "prompt copied immediately after image";
            completed.TrySetResult();
        });

        Assert.True(pump.TryEnqueue(new TestEvent(1, "image")));
        Assert.True(pump.TryEnqueue(new TestEvent(2, "text")));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pump.Stop();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("prompt copied immediately after image", appliedPrompt);
    }

    private sealed record TestEvent(int Sequence, string Kind);
}
