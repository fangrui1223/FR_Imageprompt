namespace PromptVault.App.Services;

internal enum AdaptiveGalleryTier
{
    SmallFullWarm,
    MediumMicroWarm,
    LargeRollingWindow
}

internal sealed record AdaptiveFastBrowsePlan(
    AdaptiveGalleryTier Tier,
    int MotionPixels,
    int WarmItemLimit,
    int BatchSize,
    int MotionDecodeConcurrency,
    long MotionMemoryBudgetBytes,
    TimeSpan IdleHighQualityDelay);

internal static class AdaptiveFastBrowsePolicy
{
    internal const int SmallLibraryMaximum = 1_000;
    internal const int MediumLibraryMaximum = 5_000;
    internal const int MicroPreviewPixels = 256;
    internal const int WarmBatchSize = 48;
    internal const int MotionDecodeConcurrency = 2;
    internal const long MinimumMotionMemoryBudgetBytes = 256L * 1024 * 1024;
    internal const long MaximumMotionMemoryBudgetBytes = 1024L * 1024 * 1024;
    internal static readonly TimeSpan IdleHighQualityDelay = TimeSpan.FromMilliseconds(150);

    public static AdaptiveFastBrowsePlan Create(
        long totalCount,
        long availableMemoryBytes)
    {
        var safeCount = Math.Max(0, totalCount);
        var budget = CalculateMotionMemoryBudget(availableMemoryBytes);
        var tier = safeCount <= SmallLibraryMaximum
            ? AdaptiveGalleryTier.SmallFullWarm
            : safeCount <= MediumLibraryMaximum
                ? AdaptiveGalleryTier.MediumMicroWarm
                : AdaptiveGalleryTier.LargeRollingWindow;
        var pixels = tier == AdaptiveGalleryTier.SmallFullWarm
            ? ThumbnailSizingPolicy.SmallPixels
            : MicroPreviewPixels;
        var estimatedBytesPerItem = EstimateMotionPreviewBytes(pixels);
        var budgetCapacity = Math.Max(1, budget / estimatedBytesPerItem);
        var policyCapacity = tier == AdaptiveGalleryTier.LargeRollingWindow
            ? Math.Clamp(budgetCapacity, 512, 4_096)
            : Math.Max(1, budgetCapacity);
        var warmItemLimit = (int)Math.Min(
            safeCount,
            Math.Min(policyCapacity, int.MaxValue));
        return new AdaptiveFastBrowsePlan(
            tier,
            pixels,
            warmItemLimit,
            WarmBatchSize,
            MotionDecodeConcurrency,
            budget,
            IdleHighQualityDelay);
    }

    public static long CalculateMotionMemoryBudget(long availableMemoryBytes)
    {
        if (availableMemoryBytes <= 0) return MinimumMotionMemoryBudgetBytes;
        var twoPercent = availableMemoryBytes / 50;
        return Math.Clamp(
            twoPercent,
            MinimumMotionMemoryBudgetBytes,
            MaximumMotionMemoryBudgetBytes);
    }

    private static long EstimateMotionPreviewBytes(int targetPixels)
    {
        var squareBytes = checked((long)targetPixels * targetPixels * 4);
        // Extreme portrait references can decode to more pixels than a square
        // when DecodePixelWidth is fixed. The supported 1:4..4:1 fixture mix
        // averages about 1.42 square-equivalents, so reserve 1.5x headroom.
        return squareBytes * 3 / 2;
    }
}

internal sealed class FastBrowseGenerationController : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private int _generation;
    private bool _disposed;

    public FastBrowseGenerationLease Begin(CancellationToken parentToken = default)
    {
        CancellationTokenSource? previous;
        FastBrowseGenerationLease lease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _cancellation;
            _cancellation = parentToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
                : new CancellationTokenSource();
            lease = new FastBrowseGenerationLease(++_generation, _cancellation.Token);
        }
        previous?.Cancel();
        previous?.Dispose();
        return lease;
    }

    public bool IsCurrent(int generation)
    {
        lock (_gate)
        {
            return !_disposed
                && generation == _generation
                && _cancellation?.IsCancellationRequested == false;
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _generation++;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Cancel();
    }
}

internal readonly record struct FastBrowseGenerationLease(
    int Generation,
    CancellationToken CancellationToken);

internal enum GalleryScrollDirection
{
    Backward = -1,
    None = 0,
    Forward = 1
}

internal static class FastBrowsePrefetchPlanner
{
    public static IReadOnlyList<int> CreateBackgroundWarmOrder(
        int totalCount,
        int centerIndex,
        AdaptiveFastBrowsePlan plan,
        GalleryScrollDirection direction) =>
        plan.Tier == AdaptiveGalleryTier.LargeRollingWindow
            ? CreateOrderedWindow(
                totalCount,
                centerIndex,
                plan.WarmItemLimit,
                direction)
            : Enumerable.Range(0, Math.Max(0, totalCount)).ToArray();

    public static IReadOnlyList<int> CreateOrderedWindow(
        int totalCount,
        int centerIndex,
        int itemLimit,
        GalleryScrollDirection direction)
    {
        if (totalCount <= 0 || itemLimit <= 0) return [];
        centerIndex = Math.Clamp(centerIndex, 0, totalCount - 1);
        itemLimit = Math.Min(itemLimit, totalCount);
        var result = new List<int>(itemLimit) { centerIndex };
        var distance = 1;
        while (result.Count < itemLimit)
        {
            var primary = direction == GalleryScrollDirection.Backward
                ? centerIndex - distance
                : centerIndex + distance;
            var secondary = direction == GalleryScrollDirection.Backward
                ? centerIndex + distance
                : centerIndex - distance;
            if (primary >= 0 && primary < totalCount) result.Add(primary);
            if (result.Count >= itemLimit) break;
            if (secondary >= 0 && secondary < totalCount) result.Add(secondary);
            if (primary < 0 && secondary >= totalCount) break;
            if (primary >= totalCount && secondary < 0) break;
            distance++;
        }
        return result;
    }
}
