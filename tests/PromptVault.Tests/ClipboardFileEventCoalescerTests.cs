using PromptVault.App.Services;

namespace PromptVault.Tests;

public sealed class ClipboardFileEventCoalescerTests
{
    [Fact]
    public void SameFileWithinTwoSecondsIsCoalescedEvenWhenSequenceChanges()
    {
        var filter = new ClipboardFileEventCoalescer(
            TimeSpan.FromSeconds(2),
            timestampFrequency: 1000);
        const string path = "synthetic/image.png";

        Assert.True(filter.ShouldAccept(path, 10_000));
        Assert.False(filter.ShouldAccept("SYNTHETIC/IMAGE.PNG", 11_999));
        Assert.True(filter.ShouldAccept(path, 12_001));
    }

    [Fact]
    public void DifferentFilesAreAlwaysAccepted()
    {
        var filter = new ClipboardFileEventCoalescer(
            TimeSpan.FromSeconds(2),
            timestampFrequency: 1000);

        Assert.True(filter.ShouldAccept("synthetic/one.png", 10_000));
        Assert.True(filter.ShouldAccept("synthetic/two.png", 10_001));
    }

    [Fact]
    public void CancellationResetAllowsTheSameFileImmediately()
    {
        var filter = new ClipboardFileEventCoalescer(
            TimeSpan.FromSeconds(2),
            timestampFrequency: 1000);

        Assert.True(filter.ShouldAccept("synthetic/image.png", 10_000));
        Assert.False(filter.ShouldAccept("synthetic/image.png", 10_001));

        filter.Reset();

        Assert.True(filter.ShouldAccept("synthetic/image.png", 10_002));
    }
}
