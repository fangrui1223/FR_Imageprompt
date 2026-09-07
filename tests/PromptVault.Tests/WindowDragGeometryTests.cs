using System.Windows;
using PromptVault.App.Services;

namespace PromptVault.Tests;

public sealed class WindowDragGeometryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PhysicalPointerMovementIsConvertedToDip(double scale)
    {
        var actual = WindowDragGeometry.Position(new Point(-300, 100), new Point(-1700, 400),
            new Point(-1700 + 120 * scale, 400 - 40 * scale), new DpiScale(scale, scale));
        Assert.Equal(new Point(-180, 60), actual);
    }
}
