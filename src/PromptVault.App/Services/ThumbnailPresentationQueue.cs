using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PromptVault.App.Services;

internal enum ThumbnailPresentationKind
{
    HighQuality,
    MotionPreview
}

internal static class ThumbnailPresentationQueue
{
    private sealed record Presentation(
        Action Action,
        ThumbnailRequestPriority Priority,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion,
        ThumbnailPresentationKind Kind);

    internal const int IdlePresentationsPerTick = 12;
    internal const int HighMotionPresentationsPerTick = 4;
    internal const int HighMotionPreviewPresentationsPerTick = 12;
    private static readonly ConcurrentQueue<Presentation> VisibleMotionQueue = new();
    private static readonly ConcurrentQueue<Presentation> VisibleQueue = new();
    private static readonly ConcurrentQueue<Presentation> PrefetchQueue = new();
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);
    private static DispatcherTimer? _timer;
    private static int _startRequested;
    private static long _highMotionUntil;

    public static Task PresentAsync(
        Action action,
        ThumbnailRequestPriority priority = ThumbnailRequestPriority.Visible,
        CancellationToken cancellationToken = default,
        ThumbnailPresentationKind kind = ThumbnailPresentationKind.HighQuality)
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
        var presentation = new Presentation(action, priority, cancellationToken, completion, kind);
        if (priority == ThumbnailRequestPriority.Visible
            && kind == ThumbnailPresentationKind.MotionPreview)
        {
            VisibleMotionQueue.Enqueue(presentation);
        }
        else if (priority == ThumbnailRequestPriority.Visible)
        {
            VisibleQueue.Enqueue(presentation);
        }
        else
        {
            PrefetchQueue.Enqueue(presentation);
        }
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

    internal static bool IsHighMotion =>
        Stopwatch.GetTimestamp() <= Interlocked.Read(ref _highMotionUntil);

    internal static int SelectBatchSize(bool highMotion) =>
        highMotion ? HighMotionPresentationsPerTick : IdlePresentationsPerTick;

    internal static int SelectBatchSize(bool highMotion, bool hasVisibleMotion) =>
        highMotion && hasVisibleMotion
            ? HighMotionPreviewPresentationsPerTick
            : SelectBatchSize(highMotion);

    internal static bool CanPresent(
        ThumbnailRequestPriority priority,
        bool highMotion) =>
        !highMotion || priority == ThumbnailRequestPriority.Visible;

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
        var highMotion = IsHighMotion;
        var maximumPresentations = SelectBatchSize(
            highMotion,
            !VisibleMotionQueue.IsEmpty);
        var processed = 0;
        while (processed < maximumPresentations
               && TryDequeue(highMotion, out var presentation))
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

        if (!VisibleMotionQueue.IsEmpty || !VisibleQueue.IsEmpty || !PrefetchQueue.IsEmpty) return;
        _timer?.Stop();
        Interlocked.Exchange(ref _startRequested, 0);
        if (!VisibleMotionQueue.IsEmpty || !VisibleQueue.IsEmpty || !PrefetchQueue.IsEmpty)
        {
            RequestStart(dispatcher);
        }
    }

    private static bool TryDequeue(bool highMotion, out Presentation presentation)
    {
        if (VisibleMotionQueue.TryDequeue(out presentation!)) return true;
        if (VisibleQueue.TryDequeue(out presentation!)) return true;
        if (CanPresent(ThumbnailRequestPriority.Prefetch, highMotion)
            && PrefetchQueue.TryDequeue(out presentation!))
        {
            return true;
        }
        presentation = null!;
        return false;
    }
}
