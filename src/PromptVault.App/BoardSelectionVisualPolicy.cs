namespace PromptVault.App;

internal static class BoardSelectionVisualPolicy
{
    internal static bool ShowIndividualImageBorder(bool isSelected, int selectedImageCount) =>
        isSelected && selectedImageCount > 1;

    internal static double WorldThicknessForOnePhysicalPixel(double zoom, double dpiScale) =>
        1d / (NormalizeScale(zoom) * NormalizeScale(dpiScale));

    internal static double ScreenThicknessForOnePhysicalPixel(double dpiScale) =>
        1d / NormalizeScale(dpiScale);

    private static double NormalizeScale(double value) =>
        double.IsFinite(value) && value > 0 ? value : 1d;
}
