using PromptVault.App.Services;

namespace PromptVault.Tests;

public sealed class AdaptiveFastBrowsePolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(0, (int)AdaptiveGalleryTier.SmallFullWarm, 480)]
    [InlineData(1000, (int)AdaptiveGalleryTier.SmallFullWarm, 480)]
    [InlineData(1001, (int)AdaptiveGalleryTier.MediumMicroWarm, 256)]
    [InlineData(5000, (int)AdaptiveGalleryTier.MediumMicroWarm, 256)]
    [InlineData(5001, (int)AdaptiveGalleryTier.LargeRollingWindow, 256)]
    [InlineData(30000, (int)AdaptiveGalleryTier.LargeRollingWindow, 256)]
    public void SelectsTierAndMotionPixelsAtExactBoundaries(
        long count,
        int expectedTier,
        int expectedPixels)
    {
        var plan = AdaptiveFastBrowsePolicy.Create(count, 64 * GiB);

        Assert.Equal((AdaptiveGalleryTier)expectedTier, plan.Tier);
        Assert.Equal(expectedPixels, plan.MotionPixels);
        Assert.Equal(48, plan.BatchSize);
        Assert.Equal(2, plan.MotionDecodeConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(150), plan.IdleHighQualityDelay);
        Assert.InRange(plan.WarmItemLimit, count == 0 ? 0 : 1, Math.Max(0, (int)count));
    }

    [Theory]
    [InlineData(-1, 256)]
    [InlineData(0, 256)]
    [InlineData(8, 256)]
    [InlineData(16, 327)]
    [InlineData(64, 1024)]
    [InlineData(128, 1024)]
    public void MotionBudgetUsesTwoPercentWithHardBounds(
        long availableGiB,
        long expectedMiB)
    {
        var availableBytes = availableGiB < 0 ? -1 : availableGiB * GiB;
        var budget = AdaptiveFastBrowsePolicy.CalculateMotionMemoryBudget(availableBytes);

        if (availableGiB == 16)
        {
            Assert.InRange(budget, 327L * 1024 * 1024, 328L * 1024 * 1024);
        }
        else
        {
            Assert.Equal(expectedMiB * 1024 * 1024, budget);
        }
    }

    [Fact]
    public void HighMemoryWorkstationCanWarmAllFiveHundredFiftySmallPreviews()
    {
        var plan = AdaptiveFastBrowsePolicy.Create(550, 96 * GiB);

        Assert.Equal(AdaptiveGalleryTier.SmallFullWarm, plan.Tier);
        Assert.Equal(550, plan.WarmItemLimit);
        Assert.Equal(AdaptiveFastBrowsePolicy.MaximumMotionMemoryBudgetBytes, plan.MotionMemoryBudgetBytes);
    }

    [Fact]
    public void LargeLibraryRollingWindowRemainsBounded()
    {
        var minimumMemory = AdaptiveFastBrowsePolicy.Create(30_000, 8 * GiB);
        var maximumMemory = AdaptiveFastBrowsePolicy.Create(30_000, 128 * GiB);

        Assert.InRange(minimumMemory.WarmItemLimit, 512, 4_096);
        Assert.InRange(maximumMemory.WarmItemLimit, 512, 4_096);
        Assert.True(maximumMemory.WarmItemLimit >= minimumMemory.WarmItemLimit);
    }

    [Fact]
    public void StartingANewGenerationCancelsAndInvalidatesThePreviousOne()
    {
        using var controller = new FastBrowseGenerationController();
        var first = controller.Begin();
        var second = controller.Begin();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(controller.IsCurrent(first.Generation));
        Assert.True(controller.IsCurrent(second.Generation));

        controller.Cancel();
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.False(controller.IsCurrent(second.Generation));
    }

    [Fact]
    public void PredictiveWindowPrioritizesTheCurrentScrollDirection()
    {
        var forward = FastBrowsePrefetchPlanner.CreateOrderedWindow(
            totalCount: 20,
            centerIndex: 10,
            itemLimit: 7,
            GalleryScrollDirection.Forward);
        var backward = FastBrowsePrefetchPlanner.CreateOrderedWindow(
            totalCount: 20,
            centerIndex: 10,
            itemLimit: 7,
            GalleryScrollDirection.Backward);

        Assert.Equal([10, 11, 9, 12, 8, 13, 7], forward);
        Assert.Equal([10, 9, 11, 8, 12, 7, 13], backward);
        Assert.Equal(7, forward.Distinct().Count());
        Assert.All(forward, index => Assert.InRange(index, 0, 19));
    }

    [Fact]
    public void MediumBackgroundWarmPassVisitsEveryItemDespiteCacheBudget()
    {
        var plan = AdaptiveFastBrowsePolicy.Create(5_000, 16 * GiB);

        var order = FastBrowsePrefetchPlanner.CreateBackgroundWarmOrder(
            5_000,
            2_500,
            plan,
            GalleryScrollDirection.Forward);

        Assert.Equal(5_000, order.Count);
        Assert.Equal(5_000, order.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 5_000), order);
    }

    [Fact]
    public void LargeBackgroundWarmPassStaysInsideTheRollingWindow()
    {
        var plan = AdaptiveFastBrowsePolicy.Create(30_000, 16 * GiB);

        var order = FastBrowsePrefetchPlanner.CreateBackgroundWarmOrder(
            30_000,
            15_000,
            plan,
            GalleryScrollDirection.Forward);

        Assert.Equal(plan.WarmItemLimit, order.Count);
        Assert.Equal(plan.WarmItemLimit, order.Distinct().Count());
        Assert.Equal(15_000, order[0]);
    }
}
