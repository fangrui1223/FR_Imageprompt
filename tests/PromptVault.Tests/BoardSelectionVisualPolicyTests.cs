using PromptVault.App;

namespace PromptVault.Tests;

public sealed class BoardSelectionVisualPolicyTests
{
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 4, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, true)]
    [InlineData(true, 8, true)]
    public void IndividualImageBorderOnlyAppearsForSelectedMembersOfMultiSelection(
        bool isSelected,
        int selectedImageCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            BoardSelectionVisualPolicy.ShowIndividualImageBorder(isSelected, selectedImageCount));
    }

    [Theory]
    [InlineData(0.2, 1.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, 1.5)]
    [InlineData(2.05, 1.5)]
    [InlineData(8.0, 2.0)]
    public void WorldThicknessStaysAtOnePhysicalPixel(double zoom, double dpiScale)
    {
        var thickness = BoardSelectionVisualPolicy.WorldThicknessForOnePhysicalPixel(zoom, dpiScale);

        Assert.InRange(thickness * zoom * dpiScale, 0.999999, 1.000001);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ScreenThicknessStaysAtOnePhysicalPixel(double dpiScale)
    {
        var thickness = BoardSelectionVisualPolicy.ScreenThicknessForOnePhysicalPixel(dpiScale);

        Assert.InRange(thickness * dpiScale, 0.999999, 1.000001);
    }

    [Theory]
    [InlineData(0, 1.5)]
    [InlineData(-1, 1.5)]
    [InlineData(double.NaN, 1.5)]
    [InlineData(double.PositiveInfinity, 1.5)]
    [InlineData(1.5, 0)]
    [InlineData(1.5, -1)]
    [InlineData(1.5, double.NaN)]
    [InlineData(1.5, double.PositiveInfinity)]
    public void InvalidScalesUseFinitePositiveFallback(double zoom, double dpiScale)
    {
        var world = BoardSelectionVisualPolicy.WorldThicknessForOnePhysicalPixel(zoom, dpiScale);
        var screen = BoardSelectionVisualPolicy.ScreenThicknessForOnePhysicalPixel(dpiScale);

        Assert.True(double.IsFinite(world));
        Assert.True(world > 0);
        Assert.True(double.IsFinite(screen));
        Assert.True(screen > 0);
    }
}
