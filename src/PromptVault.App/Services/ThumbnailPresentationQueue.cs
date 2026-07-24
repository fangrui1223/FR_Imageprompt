using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PromptVault.App.Services;

internal static class ThumbnailPresentationQueue
{
    private sealed record Presentation(
        Action Action,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);

    internal const int IdlePresentationsPerTick = 12;
    internal const int HighMotionPresentationsPerTick = 0;
    private static readonly ConcurrentQueue<Presentation> Queue = new();
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);
    private static DispatcherTimer? _timer;
    private static int _startRequested;
    private static long _highMotionUntil;

    public static Task PresentAsync(
        Action action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        var application = System.Windows.Application.Current;
        if (application is null)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(new Presentation(action, cancellationToken, completion));
        RequestStart(application.Dispatcher);
        return completion.Task;
    }

    public static void NotifyHighMotion(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        var until = Stopwatch.GetTimestamp()
            + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var current = Interlocked.Read(ref _highMotionUntil);
            if (current >= until) return;
            if (Interlocked.CompareExchange(ref _highMotionUntil, until, current) == current) return;
        }
    }

    internal static int SelectBatchSize(bool highMotion) =>
        highMotion ? HighMotionPresentationsPerTick : IdlePresentationsPerTick;

    private static void RequestStart(Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _startRequested, 1) != 0) return;
        _ = dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _timer ??= CreateTimer(dispatcher);
            if (!_timer.IsEnabled) _timer.Start();
        }));
    }

    private static DispatcherTimer CreateTimer(Dispatcher dispatcher)
    {
        var timer = new DispatcherTimer(
            TickInterval,
            DispatcherPriority.Render,
            (_, _) => PresentNextBatch(dispatcher),
            dispatcher);
        timer.Stop();
        return timer;
    }

    private static void PresentNextBatch(Dispatcher dispatcher)
    {
        var highMotion = Stopwatch.GetTimestamp() <= Interlocked.Read(ref _highMotionUntil);
        var maximumPresentations = SelectBatchSize(highMotion);
        var processed = 0;
        while (processed < maximumPresentations && Queue.TryDequeue(out var presentation))
        {
            processed++;
            if (presentation.CancellationToken.IsCancellationRequested)
            {
                presentation.Completion.TrySetCanceled(presentation.CancellationToken);
                continue;
            }

            try
            {
                presentation.Action();
                presentation.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                presentation.Completion.TrySetException(ex);
            }
        }

        if (!Queue.IsEmpty) return;
        _timer?.Stop();
        Interlocked.Exchange(ref _startRequested, 0);
        if (!Queue.IsEmpty) RequestStart(dispatcher);
    }
}
