using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace PromptVault.App.Services;

internal enum ThumbnailRequestPriority
{
    Visible = 0,
    Prefetch = 1
}

internal static class ThumbnailSizingPolicy
{
    public const int SmallPixels = 480;
    public const int GalleryPixels = 720;
    public const int LargeGalleryPixels = 1080;
    public const int MediumSourcePixels = 1600;

    public static int SelectTier(double layoutWidth, double imageHeight, double dpiScale)
    {
        var safeScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1d;
        var physicalPixels = Math.Max(layoutWidth, imageHeight) * safeScale;
        if (physicalPixels <= SmallPixels) return SmallPixels;
        if (physicalPixels <= GalleryPixels) return GalleryPixels;
        if (physicalPixels <= LargeGalleryPixels) return LargeGalleryPixels;
        return MediumSourcePixels;
    }
}

internal sealed record ThumbnailSchedulerSnapshot(
    long RequestCount,
    long CacheHitCount,
    long DecodeCount,
    long CoalescedRequestCount,
    long CanceledBeforeStartCount,
    int PendingCount,
    int RunningCount,
    int RunningPrefetchCount,
    int MaximumObservedConcurrency,
    int CacheEntryCount,
    long CachedBytes,
    long MemoryBudgetBytes);

internal sealed class ThumbnailScheduler : IAsyncDisposable
{
    private sealed class PendingRequest
    {
        public required string Key { get; init; }
        public required string Path { get; init; }
        public required int TargetPhysicalPixels { get; init; }
        public required TaskCompletionSource<BitmapSource> Completion { get; init; }
        public ThumbnailRequestPriority Priority { get; set; }
        public int QueueVersion { get; set; }
        public int SubscriberCount { get; set; }
        public bool Started { get; set; }
    }

    private sealed class CacheEntry
    {
        public required string RequestKey { get; init; }
        public required BitmapSource Image { get; init; }
        public required long Bytes { get; init; }
        public required LinkedListNode<string> Node { get; init; }
    }

    private readonly record struct QueueTicket(PendingRequest Request, int Version);

    private readonly object _gate = new();
    private readonly Func<string, int, BitmapSource> _decoder;
    private readonly Func<string, long> _lastWriteTicks;
    private readonly long _memoryBudgetBytes;
    private readonly int _maximumPrefetchConcurrency;
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _latestCacheKeyByRequest = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly PriorityQueue<QueueTicket, (int Priority, long Sequence)> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _queueSequence;
    private long _cachedBytes;
    private long _requestCount;
    private long _cacheHitCount;
    private long _decodeCount;
    private long _coalescedRequestCount;
    private long _canceledBeforeStartCount;
    private int _runningCount;
    private int _runningPrefetchCount;
    private int _maximumObservedConcurrency;
    private bool _disposed;

    public ThumbnailScheduler(
        int maximumConcurrency,
        long memoryBudgetBytes,
        Func<string, int, BitmapSource>? decoder = null,
        Func<string, long>? lastWriteTicks = null)
    {
        if (maximumConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        if (memoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));

        _decoder = decoder ?? ImagePipeline.LoadPreview;
        _lastWriteTicks = lastWriteTicks ?? (path => File.GetLastWriteTimeUtc(path).Ticks);
        _memoryBudgetBytes = memoryBudgetBytes;
        _maximumPrefetchConcurrency = Math.Max(1, maximumConcurrency - 1);
        _workers = Enumerable.Range(0, maximumConcurrency)
            .Select(_ => Task.Run(WorkerLoopAsync))
            .ToArray();
    }

    public Task<BitmapSource> LoadAsync(
        string path,
        int targetPhysicalPixels,
        ThumbnailRequestPriority priority,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (targetPhysicalPixels <= 0) throw new ArgumentOutOfRangeException(nameof(targetPhysicalPixels));
        cancellationToken.ThrowIfCancellationRequested();

        PendingRequest request;
        var key = CreateRequestKey(path, targetPhysicalPixels);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requestCount++;

            if (_pending.TryGetValue(key, out request!))
            {
                request.SubscriberCount++;
                _coalescedRequestCount++;
                PromoteLocked(request, priority);
            }
            else
            {
                request = new PendingRequest
                {
                    Key = key,
                    Path = Path.GetFullPath(path),
                    TargetPhysicalPixels = targetPhysicalPixels,
                    Priority = priority,
                    QueueVersion = 1,
                    SubscriberCount = 1,
                    Completion = new TaskCompletionSource<BitmapSource>(
                        TaskCreationOptions.RunContinuationsAsynchronously)
                };
                _pending.Add(key, request);
                EnqueueLocked(request);
            }
        }

        return WaitForSubscriberAsync(request, cancellationToken);
    }

    public void Promote(string path, int targetPhysicalPixels, ThumbnailRequestPriority priority)
    {
        if (string.IsNullOrWhiteSpace(path) || targetPhysicalPixels <= 0) return;
        var key = CreateRequestKey(path, targetPhysicalPixels);
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending.TryGetValue(key, out var request)) PromoteLocked(request, priority);
        }
    }

    public ThumbnailSchedulerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new ThumbnailSchedulerSnapshot(
                _requestCount,
                _cacheHitCount,
                _decodeCount,
                _coalescedRequestCount,
                _canceledBeforeStartCount,
                _pending.Values.Count(request => !request.Started),
                _runningCount,
                _runningPrefetchCount,
                _maximumObservedConcurrency,
                _cache.Count,
                _cachedBytes,
                _memoryBudgetBytes);
        }
    }

    private async Task<BitmapSource> WaitForSubscriberAsync(
        PendingRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return cancellationToken.CanBeCanceled
                ? await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await request.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            ReleaseSubscriber(request);
        }
    }

    private void ReleaseSubscriber(PendingRequest request)
    {
        var canceledBeforeStart = false;
        lock (_gate)
        {
            if (request.SubscriberCount > 0) request.SubscriberCount--;
            if (request.SubscriberCount != 0
                || request.Started
                || !_pending.TryGetValue(request.Key, out var current)
                || !ReferenceEquals(current, request))
            {
                return;
            }

            _pending.Remove(request.Key);
            request.QueueVersion++;
            _canceledBeforeStartCount++;
            canceledBeforeStart = true;
        }

        if (canceledBeforeStart)
        {
            request.Completion.TrySetCanceled();
        }
    }

    private void PromoteLocked(PendingRequest request, ThumbnailRequestPriority priority)
    {
        if (request.Started || priority >= request.Priority) return;
        request.Priority = priority;
        request.QueueVersion++;
        EnqueueLocked(request);
    }

    private void EnqueueLocked(PendingRequest request)
    {
        var sequence = _queueSequence++;
        _queue.Enqueue(
            new QueueTicket(request, request.QueueVersion),
            ((int)request.Priority, sequence));
        _queueSignal.Release();
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                await _queueSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                PendingRequest? request = null;
                lock (_gate)
                {
                    while (_queue.TryPeek(out var ticket, out _))
                    {
                        if (ticket.Version != ticket.Request.QueueVersion
                            || ticket.Request.Started
                            || !_pending.TryGetValue(ticket.Request.Key, out var current)
                            || !ReferenceEquals(current, ticket.Request))
                        {
                            _queue.Dequeue();
                            continue;
                        }

                        if (ticket.Request.Priority == ThumbnailRequestPriority.Prefetch
                            && _runningPrefetchCount >= _maximumPrefetchConcurrency)
                        {
                            break;
                        }

                        _queue.Dequeue();
                        request = ticket.Request;
                        request.Started = true;
                        _runningCount++;
                        if (request.Priority == ThumbnailRequestPriority.Prefetch)
                        {
                            _runningPrefetchCount++;
                        }
                        _maximumObservedConcurrency = Math.Max(_maximumObservedConcurrency, _runningCount);
                        break;
                    }
                }

                if (request is null) continue;
                Decode(request);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void Decode(PendingRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var decodeAttempted = false;
        try
        {
            var lastWriteTicks = _lastWriteTicks(request.Path);
            var cacheKey = CreateCacheKey(request.Key, lastWriteTicks);
            BitmapSource? cachedImage = null;
            ThumbnailSchedulerSnapshot? cacheSnapshot = null;
            lock (_gate)
            {
                if (_cache.TryGetValue(cacheKey, out var cached))
                {
                    TouchCacheEntry(cached);
                    _cacheHitCount++;
                    FinishStartedRequestLocked(request);
                    cacheSnapshot = GetSnapshotLocked();
                    cachedImage = cached.Image;
                }
            }

            if (cachedImage is not null)
            {
                stopwatch.Stop();
                request.Completion.TrySetResult(cachedImage);
                DevelopmentPerformanceTrace.Event("thumbnail-load", new
                {
                    cacheHit = true,
                    priority = request.Priority.ToString(),
                    targetPhysicalPixels = request.TargetPhysicalPixels,
                    elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                    running = cacheSnapshot!.RunningCount,
                    pending = cacheSnapshot.PendingCount,
                    cachedMiB = Math.Round(cacheSnapshot.CachedBytes / 1024d / 1024d, 3)
                });
                return;
            }

            decodeAttempted = true;
            var image = _decoder(request.Path, request.TargetPhysicalPixels);
            if (!image.IsFrozen) image.Freeze();
            stopwatch.Stop();
            ThumbnailSchedulerSnapshot snapshot;
            lock (_gate)
            {
                _runningCount--;
                if (request.Priority == ThumbnailRequestPriority.Prefetch) _runningPrefetchCount--;
                _decodeCount++;
                RemovePendingLocked(request);
                AddCacheEntryLocked(request.Key, cacheKey, image);
                WakeWorkerForQueuedRequestLocked();
                snapshot = GetSnapshotLocked();
            }

            request.Completion.TrySetResult(image);
            DevelopmentPerformanceTrace.Event("thumbnail-load", new
            {
                cacheHit = false,
                priority = request.Priority.ToString(),
                targetPhysicalPixels = request.TargetPhysicalPixels,
                elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                running = snapshot.RunningCount,
                pending = snapshot.PendingCount,
                cachedMiB = Math.Round(snapshot.CachedBytes / 1024d / 1024d, 3)
            });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            lock (_gate)
            {
                _runningCount--;
                if (request.Priority == ThumbnailRequestPriority.Prefetch) _runningPrefetchCount--;
                if (decodeAttempted) _decodeCount++;
                RemovePendingLocked(request);
                WakeWorkerForQueuedRequestLocked();
            }
            request.Completion.TrySetException(ex);
            DevelopmentPerformanceTrace.Event("thumbnail-load-failed", new
            {
                exception = ex.GetType().Name,
                elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)
            });
        }
    }

    private void RemovePendingLocked(PendingRequest request)
    {
        if (_pending.TryGetValue(request.Key, out var current) && ReferenceEquals(current, request))
        {
            _pending.Remove(request.Key);
        }
    }

    private void WakeWorkerForQueuedRequestLocked()
    {
        if (_queue.Count > 0) _queueSignal.Release();
    }

    private void FinishStartedRequestLocked(PendingRequest request)
    {
        _runningCount--;
        if (request.Priority == ThumbnailRequestPriority.Prefetch) _runningPrefetchCount--;
        RemovePendingLocked(request);
        WakeWorkerForQueuedRequestLocked();
    }

    private void AddCacheEntryLocked(string requestKey, string cacheKey, BitmapSource image)
    {
        var bytes = EstimateDecodedBytes(image);
        if (bytes > _memoryBudgetBytes) return;

        if (_latestCacheKeyByRequest.TryGetValue(requestKey, out var previousCacheKey)
            && !string.Equals(previousCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            RemoveCacheEntryLocked(previousCacheKey);
        }
        if (_cache.Remove(cacheKey, out var existing))
        {
            _lru.Remove(existing.Node);
            _cachedBytes -= existing.Bytes;
        }

        var node = _lru.AddFirst(cacheKey);
        _cache.Add(cacheKey, new CacheEntry
        {
            RequestKey = requestKey,
            Image = image,
            Bytes = bytes,
            Node = node
        });
        _latestCacheKeyByRequest[requestKey] = cacheKey;
        _cachedBytes += bytes;
        while (_cachedBytes > _memoryBudgetBytes && _lru.Last is { } last)
        {
            var evictedKey = last.Value;
            RemoveCacheEntryLocked(evictedKey);
        }
    }

    private void RemoveCacheEntryLocked(string cacheKey)
    {
        if (!_cache.Remove(cacheKey, out var entry)) return;
        _lru.Remove(entry.Node);
        _cachedBytes -= entry.Bytes;
        if (_latestCacheKeyByRequest.TryGetValue(entry.RequestKey, out var latest)
            && string.Equals(latest, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            _latestCacheKeyByRequest.Remove(entry.RequestKey);
        }
    }

    private void TouchCacheEntry(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private ThumbnailSchedulerSnapshot GetSnapshotLocked() => new(
        _requestCount,
        _cacheHitCount,
        _decodeCount,
        _coalescedRequestCount,
        _canceledBeforeStartCount,
        _pending.Values.Count(request => !request.Started),
        _runningCount,
        _runningPrefetchCount,
        _maximumObservedConcurrency,
        _cache.Count,
        _cachedBytes,
        _memoryBudgetBytes);

    private static string CreateRequestKey(string path, int targetPhysicalPixels) =>
        $"{Path.GetFullPath(path)}\0{targetPhysicalPixels}";

    private static string CreateCacheKey(string requestKey, long lastWriteTicks) =>
        $"{requestKey}\0{lastWriteTicks}";

    private static long EstimateDecodedBytes(BitmapSource image)
    {
        var bytesPerPixel = Math.Max(1, (image.Format.BitsPerPixel + 7) / 8);
        return checked((long)image.PixelWidth * image.PixelHeight * bytesPerPixel);
    }

    public async ValueTask DisposeAsync()
    {
        PendingRequest[] abandoned;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            abandoned = _pending.Values.Where(request => !request.Started).ToArray();
            foreach (var request in abandoned)
            {
                _pending.Remove(request.Key);
                request.QueueVersion++;
            }
        }

        foreach (var request in abandoned) request.Completion.TrySetCanceled();
        _shutdown.Cancel();
        _queueSignal.Release(_workers.Length);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
        _queueSignal.Dispose();
    }
}

public static class ThumbnailCache
{
    public const long DefaultMemoryBudgetBytes = 384L * 1024 * 1024;
    private static readonly ThumbnailScheduler Scheduler = new(
        Math.Clamp(Environment.ProcessorCount / 8, 2, 4),
        DefaultMemoryBudgetBytes);

    internal static Task<BitmapSource> LoadAsync(
        string path,
        int targetPhysicalPixels,
        ThumbnailRequestPriority priority,
        CancellationToken cancellationToken) =>
        Scheduler.LoadAsync(path, targetPhysicalPixels, priority, cancellationToken);

    internal static void Promote(
        string path,
        int targetPhysicalPixels,
        ThumbnailRequestPriority priority) =>
        Scheduler.Promote(path, targetPhysicalPixels, priority);

    internal static ThumbnailSchedulerSnapshot GetSnapshot() => Scheduler.GetSnapshot();
}
