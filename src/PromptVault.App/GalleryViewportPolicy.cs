namespace PromptVault.App;

internal enum GalleryBringIntoViewIntent
{
    PointerCardInteraction,
    ExplicitSelectionNavigation
}

internal static class GalleryViewportPolicy
{
    internal static bool ShouldBringIntoView(GalleryBringIntoViewIntent intent) =>
        intent == GalleryBringIntoViewIntent.ExplicitSelectionNavigation;
}
