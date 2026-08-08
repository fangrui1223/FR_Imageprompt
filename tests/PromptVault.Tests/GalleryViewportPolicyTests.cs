using PromptVault.App;

namespace PromptVault.Tests;

public sealed class GalleryViewportPolicyTests
{
    [Fact]
    public void PointerCardInteractionDoesNotBringPartialCardIntoView()
    {
        Assert.False(GalleryViewportPolicy.ShouldBringIntoView(
            GalleryBringIntoViewIntent.PointerCardInteraction));
    }

    [Fact]
    public void ExplicitSelectionNavigationStillBringsTargetIntoView()
    {
        Assert.True(GalleryViewportPolicy.ShouldBringIntoView(
            GalleryBringIntoViewIntent.ExplicitSelectionNavigation));
    }
}
