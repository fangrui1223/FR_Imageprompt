using PromptVault.App.Services;

namespace PromptVault.Tests;

public sealed class BoardProgressiveImagePolicyTests
{
    [Theory]
    [InlineData(400, 300, 1.5, 720)]
    [InlineData(600, 400, 1.5, 1080)]
    [InlineData(900, 600, 1.5, 1600)]
    [InlineData(1600, 1000, 1.5, 2400)]
    [InlineData(3000, 2000, 1.5, 2800)]
    public void DecodeWidthUsesBoundedPhysicalPixelTiers(
        double width, double height, double dpi, int expected) =>
        Assert.Equal(expected, BoardProgressiveImagePolicy.SelectDecodeWidth(width, height, dpi));

    [Fact]
    public void SignificantAreaRejectsTinyMultiSelectionMembers()
    {
        Assert.False(BoardProgressiveImagePolicy.IsSignificant(100, 100, 1.5));
        Assert.True(BoardProgressiveImagePolicy.IsSignificant(400, 300, 1.5));
        Assert.Equal(6, BoardProgressiveImagePolicy.MaximumMultiSelectionUpgrades);
    }
}
