using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.App;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class ThumbnailSchedulerTests
{
    [Fact]
    public void PresentationBatchShrinksDuringHighMotion()
    {
        Assert.Equal(
            ThumbnailPresentationQueue.IdlePresentationsPerTick,
            ThumbnailPresentationQueue.SelectBatchSize(highMotion: false));
        Assert.Equal(
            ThumbnailPresentationQueue.HighMotionPresentationsPerTick,
            ThumbnailPresentationQueue.SelectBatchSize(highMotion: true));
        Assert.True(
            ThumbnailPresentationQueue.HighMotionPresentationsPerTick
            < ThumbnailPresentationQueue.IdlePresentationsPerTick);
    }

    [Fact]
    public async Task ImmersiveCacheReusesDecodedImage()
    {
        var decodeCount = 0;
        var image = CreateBitmap(120, 80);
        var cache = new ImmersiveImageCache(
            4 * 1024 * 1024,
            (_, _) =>
            {
                Interlocked.Increment(ref decodeCount);
                return image;
            },
            _ => 123);

        var first = await cache.LoadAsync(TestPath("immersive-cache.jpg"), 1600);
        var second = await cache.LoadAsync(TestPath("immersive-cache.jpg"), 1600);
        var snapshot = cache.GetSnapshot();

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Same(first.Image, second.Image);
        Assert.Equal(1, decodeCount);
        Assert.Equal(1, snapshot.DecodeCount);
        Assert.Equal(1, snapshot.CacheHitCount);
        Assert.True(snapshot.CachedBytes <= snapshot.MemoryBudgetBytes);
    }

    [Fact]
    public async Task ImmersiveCacheCoalescesConcurrentRequests()
    {
        var decoderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDecoder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decodeCount = 0;
        var image = CreateBitmap(120, 80);
        var cache = new ImmersiveImageCache(
            4 * 1024 * 1024,
            (_, _) =>
            {
                Interlocked.Increment(ref decodeCount);
                decoderStarted.TrySetResult();
                releaseDecoder.Task.GetAwaiter().GetResult();
                return image;
            },
            _ => 123);

        var first = cache.LoadAsync(TestPath("immersive-coalesce.jpg"), 1600);
        await decoderStarted.Task;
        var second = cache.LoadAsync(TestPath("immersive-coalesce.jpg"), 1600);
        releaseDecoder.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, decodeCount);
        Assert.Equal(1, cache.GetSnapshot().CoalescedRequestCount);
    }

    [Fact]
    public async Task ImmersiveCacheEvictsByDecodedBytes()
    {
        var decodeCount = 0;
        var image = CreateBitmap(100, 100);
        var cache = new ImmersiveImageCache(
            50_000,
            (_, _) =>
            {
                Interlocked.Increment(ref decodeCount);
                return image;
            },
            _ => 123);

        await cache.LoadAsync(TestPath("immersive-first.jpg"), 1600);
        await cache.LoadAsync(TestPath("immersive-second.jpg"), 1600);
        await cache.LoadAsync(TestPath("immersive-first.jpg"), 1600);

        var snapshot = cache.GetSnapshot();
        Assert.Equal(3, decodeCount);
        Assert.Equal(1, snapshot.EntryCount);
        Assert.True(snapshot.CachedBytes <= snapshot.MemoryBudgetBytes);
    }

    [Theory]
    [InlineData(300, 260, 1.5, 480)]
    [InlineData(340, 260, 1.5, 720)]
    [InlineData(480, 320, 1.0, 480)]
    [InlineData(481, 320, 1.0, 720)]
    [InlineData(720, 320, 1.5, 1080)]
    [InlineData(721, 320, 1.5, 1600)]
    public void SizingPolicy_UsesPhysicalCardPixels(
        double layoutWidth,
        double imageHeight,
        double dpiScale,
        int expectedTier)
    {
        Assert.Equal(
            expectedTier,
            ThumbnailSizingPolicy.SelectTier(layoutWidth, imageHeight, dpiScale));
    }

    [Fact]
    public async Task Scheduler_BoundsConcurrentDecodes()
    {
        using var release = new ManualResetEventSlim();
        using var firstWorkersStarted = new CountdownEvent(2);
        var active = 0;
        var maximum = 0;
        var startedCount = 0;
        var concurrencyGate = new object();
        await using var scheduler = new ThumbnailScheduler(
            maximumConcurrency: 2,
            memoryBudgetBytes: 8 * 1024 * 1024,
            decoder: (_, _) =>
            {
                var current = Interlocked.Increment(ref active);
                lock (concurrencyGate) maximum = Math.Max(maximum, current);
                if (Interlocked.Increment(ref startedCount) <= 2) firstWorkersStarted.Signal();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                Interlocked.Decrement(ref active);
                return CreateBitmap(16, 16);
            });

        var tasks = Enumerable.Range(0, 8)
            .Select(index => scheduler.LoadAsync(
                TestPath($"bounded-{index}.png"),
                520,
                ThumbnailRequestPriority.Visible))
            .ToArray();

        Assert.True(firstWorkersStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, maximum);
        release.Set();
        await Task.WhenAll(tasks);

        var snapshot = scheduler.GetSnapshot();
        Assert.Equal(8, snapshot.DecodeCount);
        Assert.Equal(2, snapshot.MaximumObservedConcurrency);
    }

    [Fact]
    public async Task Scheduler_ReservesDecodeCapacityForNewlyVisibleWork()
    {
        using var release = new ManualResetEventSlim();
        using var prefetchStarted = new CountdownEvent(2);
        using var visibleStarted = new ManualResetEventSlim();
        var prefetchStartCount = 0;
        await using var scheduler = new ThumbnailScheduler(
            maximumConcurrency: 3,
            memoryBudgetBytes: 8 * 1024 * 1024,
            decoder: (path, _) =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("reserve-prefetch", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref prefetchStartCount) <= 2) prefetchStarted.Signal();
                }
                else if (name == "reserve-visible")
                {
                    visibleStarted.Set();
                }

                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                return CreateBitmap(16, 16);
            });

        var prefetchTasks = Enumerable.Range(0, 6)
            .Select(index => scheduler.LoadAsync(
                TestPath($"reserve-prefetch-{index}.png"),
                520,
                ThumbnailRequestPriority.Prefetch))
            .ToArray();
        Assert.True(prefetchStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, scheduler.GetSnapshot().RunningPrefetchCount);

        var visible = scheduler.LoadAsync(
            TestPath("reserve-visible.png"),
            520,
            ThumbnailRequestPriority.Visible);
        Assert.True(visibleStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, scheduler.GetSnapshot().RunningCount);

        release.Set();
        await Task.WhenAll(prefetchTasks.Append(visible));
    }

    [Fact]
    public async Task Scheduler_PrioritizesVisibleRequestsAheadOfQueuedPrefetch()
    {
        using var releaseFirst = new ManualResetEventSlim();
        using var firstStarted = new ManualResetEventSlim();
        var decodeOrder = new ConcurrentQueue<string>();
        await using var scheduler = new ThumbnailScheduler(
            maximumConcurrency: 1,
            memoryBudgetBytes: 8 * 1024 * 1024,
            decoder: (path, _) =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                decodeOrder.Enqueue(name);
                if (name == "priority-blocker")
                {
                    firstStarted.Set();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                }
                return CreateBitmap(16, 16);
            });

        var blocker = scheduler.LoadAsync(
            TestPath("priority-blocker.png"),
            520,
            ThumbnailRequestPriority.Visible);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        var prefetch = scheduler.LoadAsync(
            TestPath("priority-prefetch.png"),
            520,
            ThumbnailRequestPriority.Prefetch);
        var visible = scheduler.LoadAsync(
            TestPath("priority-visible.png"),
            520,
            ThumbnailRequestPriority.Visible);

        releaseFirst.Set();
        await Task.WhenAll(blocker, prefetch, visible);

        Assert.Equal(
            ["priority-blocker", "priority-visible", "priority-prefetch"],
            decodeOrder.ToArray());
    }

    [Fact]
    public async Task Scheduler_CoalescesSamePathAndDecodeWidth()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var decodeCount = 0;
        await using var scheduler = new ThumbnailScheduler(
            maximumConcurrency: 2,
            memoryBudgetBytes: 8 * 1024 * 1024,
            decoder: (_, _) =>
            {
                Interlocked.Increment(ref decodeCount);
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                return CreateBitmap(16, 16);
            });

        var path = TestPath("coalesced.png");
        var first = scheduler.LoadAsync(path, 520, ThumbnailRequestPriority.Prefetch);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var second = scheduler.LoadAsync(path, 520, ThumbnailRequestPriority.Visible);
        release.Set();

        var images = await Task.WhenAll(first, second);
        Assert.Equal(1, decodeCount);
        Assert.Same(images[0], images[1]);
        Assert.Equal(1, scheduler.GetSnapshot().CoalescedRequestCount);
    }

    [Fact]
    public async Task Scheduler_CancelsPendingDecodeWhenLastSubscriberLeaves()
    {
        using var releaseFirst = new ManualResetEventSlim();
        using var firstStarted = new ManualResetEventSlim();
        var decodedNames = new ConcurrentQueue<string>();
        await using var scheduler = new ThumbnailScheduler(
            maximumConcurrency: 1,
            memoryBudgetBytes: 8 * 1024 * 1024,
            decoder: (path, _) =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                decodedNames.Enqueue(name);
                if (name == "cancel-blocker")
                {
                    firstStarted.Set();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                }
                return CreateBitmap(16, 16);
            });

        var blocker = scheduler.LoadAsync(
            TestPath("cancel-blocker.png"),
            520,
            ThumbnailRequestPriority.Visible);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        using var cancellation = new CancellationTokenSource();
        var pending = scheduler.LoadAsync(
            TestPath("cancel-pending.png"),
            520,
            ThumbnailRequestPriority.Prefetch,
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        releaseFirst.Set();
        await blocker;

        Assert.DoesNotContain("cancel-pending", decodedNames);
        Assert.Equal(1, scheduler.GetSnapshot().CanceledBeforeStartCount);
    }

    [Fact]
    public async Task Scheduler_EvictsByDecodedPixelMemoryBudget()
    {
        var decodeCount = 0;
        const int oneImageBytes = 16 * 16 * 4;
        await using var scheduler = new ThumbnailScheduler(
            maximumConcurrency: 1,
            memoryBudgetBytes: oneImageBytes + 128,
            decoder: (_, _) =>
            {
                Interlocked.Increment(ref decodeCount);
                return CreateBitmap(16, 16);
            });

        var firstPath = TestPath("budget-first.png");
        await scheduler.LoadAsync(firstPath, 520, ThumbnailRequestPriority.Visible);
        await scheduler.LoadAsync(TestPath("budget-second.png"), 520, ThumbnailRequestPriority.Visible);
        await scheduler.LoadAsync(firstPath, 520, ThumbnailRequestPriority.Visible);

        var snapshot = scheduler.GetSnapshot();
        Assert.Equal(3, decodeCount);
        Assert.Equal(1, snapshot.CacheEntryCount);
        Assert.True(snapshot.CachedBytes <= snapshot.MemoryBudgetBytes);
    }

    [Fact]
    public async Task Scheduler_InvalidatesCacheWhenFileModificationChanges()
    {
        var decodeCount = 0;
        long lastWriteTicks = 10;
        await using var scheduler = new ThumbnailScheduler(
            maximumConcurrency: 1,
            memoryBudgetBytes: 8 * 1024 * 1024,
            decoder: (_, _) =>
            {
                Interlocked.Increment(ref decodeCount);
                return CreateBitmap(16, 16);
            },
            lastWriteTicks: _ => Interlocked.Read(ref lastWriteTicks));

        var path = TestPath("modified.png");
        await scheduler.LoadAsync(path, 480, ThumbnailRequestPriority.Visible);
        await scheduler.LoadAsync(path, 480, ThumbnailRequestPriority.Visible);
        Assert.Equal(1, decodeCount);
        Assert.Equal(1, scheduler.GetSnapshot().CacheHitCount);

        Interlocked.Exchange(ref lastWriteTicks, 20);
        await scheduler.LoadAsync(path, 480, ThumbnailRequestPriority.Visible);

        var snapshot = scheduler.GetSnapshot();
        Assert.Equal(2, decodeCount);
        Assert.Equal(1, snapshot.CacheEntryCount);
    }

    [Fact]
    public async Task CardViewModel_UsesStableFailurePlaceholderWithoutThrowing()
    {
        var missing = TestPath($"missing-{Guid.NewGuid():N}.jpg");
        var entry = new GalleryEntry(
            1,
            "missing",
            missing,
            missing,
            missing,
            800,
            600,
            "jpg",
            "",
            "",
            null,
            "",
            "",
            DateTimeOffset.UtcNow,
            null,
            true,
            null);
        var card = new GalleryCardViewModel(
            entry,
            new LibraryPaths(Path.GetTempPath()),
            layoutWidth: 280,
            imageHeight: 210);

        await card.LoadAsync(ThumbnailRequestPriority.Visible, dpiScale: 1.5);

        Assert.Null(card.Thumbnail);
        Assert.True(card.ThumbnailLoadFailed);
    }

    private static string TestPath(string fileName) =>
        Path.Combine(Path.GetTempPath(), "PromptVault-ThumbnailSchedulerTests", fileName);

    private static BitmapSource CreateBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var image = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        image.Freeze();
        return image;
    }
}
