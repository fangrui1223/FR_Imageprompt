namespace PromptVault.App.Services;

internal static class BoardProgressiveImagePolicy
{
    public const int MaximumMultiSelectionUpgrades = 6;
    public const double MinimumSignificantPhysicalArea = 180_000;

    public static int SelectDecodeWidth(double displayWidthDip, double displayHeightDip, double dpiScale)
    {
        var physical = Math.Max(displayWidthDip, displayHeightDip)
            * (double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1);
        if (physical <= 720) return 720;
        if (physical <= 1080) return 1080;
        if (physical <= 1600) return 1600;
        return (int)Math.Clamp(Math.Ceiling(physical / 400) * 400, 2000, 2800);
    }

    public static bool IsSignificant(double widthDip, double heightDip, double dpiScale)
    {
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        return widthDip * scale * heightDip * scale >= MinimumSignificantPhysicalArea;
    }
}
