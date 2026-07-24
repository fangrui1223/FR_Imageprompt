using System.Windows.Media.Imaging;

namespace PromptVault.App.Services;

internal sealed record ImmersiveImageLoadResult(
    BitmapSource Image,
    bool CacheHit);

internal sealed record ImmersiveImageCacheSnapshot(
    long RequestCount,
    long CacheHitCount,
    long DecodeCount,
    long CoalescedRequestCount,
    int EntryCount,
    long CachedBytes,
    long MemoryBudgetBytes);

internal sealed class ImmersiveImageCache
{
    private sealed class CacheEntry
    {
        public required BitmapSource Image { get; init; }
        public required long Bytes { get; init; }
        public required LinkedListNode<string> Node { get; init; }
    }

    public const long DefaultMemoryBudgetBytes = 192L * 1024 * 1024;
    public static ImmersiveImageCache Shared { get; } = new(DefaultMemoryBudgetBytes);

    private readonly object _gate = new();
    private readonly long _memoryBudgetBytes;
    private readonly Func<string, int, BitmapSource> _decoder;
    private readonly Func<string, long> _lastWriteTicks;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<BitmapSource>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private long _cachedBytes;
    private long _requestCount;
    private long _cacheHitCount;
    private long _decodeCount;
    private long _coalescedRequestCount;

    public ImmersiveImageCache(
        long memoryBudgetBytes,
        Func<string, int, BitmapSource>? decoder = null,
        Func<string, long>? lastWriteTicks = null)
    {
        if (memoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        _memoryBudgetBytes = memoryBudgetBytes;
        _decoder = decoder ?? ImagePipeline.LoadPreview;
        _lastWriteTicks = lastWriteTicks ?? (path => File.GetLastWriteTimeUtc(path).Ticks);
    }

    public async Task<ImmersiveImageLoadResult> LoadAsync(
        string path,
        int decodeWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (decodeWidth <= 0) throw new ArgumentOutOfRangeException(nameof(decodeWidth));
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var key = CreateKey(fullPath, decodeWidth, _lastWriteTicks(fullPath));
        Task<BitmapSource> pending;
        var attachCompletion = false;
        lock (_gate)
        {
            _requestCount++;
            if (_cache.TryGetValue(key, out var cached))
            {
                TouchLocked(cached);
                _cacheHitCount++;
                return new ImmersiveImageLoadResult(cached.Image, CacheHit: true);
            }

            if (_pending.TryGetValue(key, out pending!))
            {
                _coalescedRequestCount++;
            }
            else
            {
                pending = Task.Run(() => _decoder(fullPath, decodeWidth));
                _pending.Add(key, pending);
                attachCompletion = true;
            }
        }

        if (attachCompletion)
        {
            _ = pending.ContinueWith(
                completed => CompleteDecode(key, pending, completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        var image = cancellationToken.CanBeCanceled
            ? await pending.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await pending.ConfigureAwait(false);
        return new ImmersiveImageLoadResult(image, CacheHit: false);
    }

    public async Task PrefetchAsync(string path, int decodeWidth)
    {
        try
        {
            await LoadAsync(path, decodeWidth).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DevelopmentPerformanceTrace.Event("immersive-prefetch-failed", new
            {
                exception = ex.GetType().Name
            });
        }
    }

    public ImmersiveImageCacheSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new ImmersiveImageCacheSnapshot(
                _requestCount,
                _cacheHitCount,
                _decodeCount,
                _coalescedRequestCount,
                _cache.Count,
                _cachedBytes,
                _memoryBudgetBytes);
        }
    }

    private void CompleteDecode(
        string key,
        Task<BitmapSource> pending,
        Task<BitmapSource> completed)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
            {
                _pending.Remove(key);
                _decodeCount++;
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    AddLocked(key, completed.Result);
                }
            }
        }
    }

    private void AddLocked(string key, BitmapSource image)
    {
        if (!image.IsFrozen) image.Freeze();
        if (_cache.TryGetValue(key, out var existing))
        {
            TouchLocked(existing);
            return;
        }

        var bytes = EstimateDecodedBytes(image);
        if (bytes > _memoryBudgetBytes) return;
        var node = _lru.AddFirst(key);
        _cache.Add(key, new CacheEntry
        {
            Image = image,
            Bytes = bytes,
            Node = node
        });
        _cachedBytes += bytes;
        while (_cachedBytes > _memoryBudgetBytes && _lru.Last is { } last)
        {
            var evictedKey = last.Value;
            _lru.RemoveLast();
            if (_cache.Remove(evictedKey, out var evicted)) _cachedBytes -= evicted.Bytes;
        }
    }

    private void TouchLocked(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private static string CreateKey(string path, int decodeWidth, long lastWriteTicks) =>
        $"{path}\0{decodeWidth}\0{lastWriteTicks}";

    private static long EstimateDecodedBytes(BitmapSource image)
    {
        var bytesPerPixel = Math.Max(1, (image.Format.BitsPerPixel + 7) / 8);
        return checked((long)image.PixelWidth * image.PixelHeight * bytesPerPixel);
    }
}
