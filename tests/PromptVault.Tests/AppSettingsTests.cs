using PromptVault.App;
using PromptVault.App.Services;

namespace PromptVault.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void CorruptSettingsArePreservedBeforeDefaultsAreSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, "{ invalid json");
        try
        {
            var settings = AppSettings.Load(path);

            Assert.NotNull(settings.RecoveryNotice);
            Assert.NotNull(settings.RecoveryBackupPath);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(settings.RecoveryBackupPath));
            Assert.Equal("{ invalid json", File.ReadAllText(settings.RecoveryBackupPath!));

            settings.LibraryRoot = "D:\\Example";
            settings.Save();

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(settings.RecoveryBackupPath));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ReducedMotionPreferenceRoundTripsInIsolatedSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            var settings = AppSettings.Load(path);
            settings.LibraryRoot = "D:\\SyntheticLibrary";
            settings.CaptureListeningEnabled = false;
            settings.ReducedMotionEnabled = true;
            settings.InspectorPinned = true;
            settings.EdgeMenuSensitivity = EdgeIntentProfile.HighSensitivity;
            settings.EdgeMenusAlwaysVisible = true;
            settings.Save();

            var reloaded = AppSettings.Load(path);

            Assert.True(reloaded.ReducedMotionEnabled);
            Assert.True(reloaded.InspectorPinned);
            Assert.Equal(EdgeIntentProfile.HighSensitivity, reloaded.EdgeMenuSensitivity);
            Assert.True(reloaded.EdgeMenusAlwaysVisible);
            Assert.False(reloaded.CaptureListeningEnabled);
            Assert.Equal("D:\\SyntheticLibrary", reloaded.LibraryRoot);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void EdgeIntentRejectsFastPassAndRevealsAfterConfiguredDwell()
    {
        var detector = new EdgeIntentDetector();
        var normal = EdgeIntentProfile.FromSensitivity(EdgeIntentProfile.NormalSensitivity);

        detector.Observe(120, 80, 1000, 700, TimeSpan.Zero, normal);
        detector.Observe(8, 80, 1000, 700, TimeSpan.FromMilliseconds(20), normal);
        detector.Observe(80, 80, 1000, 700, TimeSpan.FromMilliseconds(55), normal);

        Assert.Equal(
            EdgeIntentEdge.None,
            detector.Poll(80, 80, 1000, 700, TimeSpan.FromMilliseconds(160), normal));

        detector.Observe(10, 160, 1000, 700, TimeSpan.FromMilliseconds(200), normal);
        Assert.Equal(
            EdgeIntentEdge.None,
            detector.Poll(10, 160, 1000, 700, TimeSpan.FromMilliseconds(240), normal));
        Assert.Equal(
            EdgeIntentEdge.Left,
            detector.Poll(10, 160, 1000, 700, TimeSpan.FromMilliseconds(320), normal));
    }

    [Fact]
    public void EdgeIntentSensitivityMapsToNinetyThroughOneHundredFortyMilliseconds()
    {
        var low = EdgeIntentProfile.FromSensitivity(EdgeIntentProfile.LowSensitivity);
        var normal = EdgeIntentProfile.FromSensitivity(EdgeIntentProfile.NormalSensitivity);
        var high = EdgeIntentProfile.FromSensitivity(EdgeIntentProfile.HighSensitivity);

        Assert.Equal(140, low.Dwell.TotalMilliseconds);
        Assert.Equal(115, normal.Dwell.TotalMilliseconds);
        Assert.Equal(90, high.Dwell.TotalMilliseconds);
        Assert.True(low.MaximumApproachSpeed < normal.MaximumApproachSpeed);
        Assert.True(normal.MaximumApproachSpeed < high.MaximumApproachSpeed);
    }

    [Fact]
    public void ReducedMotionResolvesEveryVisualDurationToZero()
    {
        foreach (var token in Enum.GetValues<MotionToken>())
        {
            Assert.Equal(TimeSpan.Zero, VisualModeService.ResolveDuration(token, reducedMotion: true));
            Assert.True(VisualModeService.ResolveDuration(token, reducedMotion: false) > TimeSpan.Zero);
        }
    }

    [Fact]
    public void LayoutPreferencesRoundTripIndependentlyForLibraryAndExternalFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            var settings = AppSettings.Load(path);
            var library = settings.GetGalleryLayout("library");
            library.Mode = GalleryLayoutPreference.JustifiedMode;
            library.Spacing = 8;
            library.TargetSize = 240;
            var external = settings.GetGalleryLayout("external:synthetic");
            external.Mode = GalleryLayoutPreference.WaterfallMode;
            external.ApplyDensity(GalleryLayoutPreference.SpaciousDensity);
            settings.Save();

            var reloaded = AppSettings.Load(path);

            Assert.Equal(GalleryLayoutMode.Justified, reloaded.GetGalleryLayout("library").ToOptions().Mode);
            Assert.Equal(8, reloaded.GetGalleryLayout("library").Spacing);
            Assert.Equal(240, reloaded.GetGalleryLayout("library").TargetSize);
            Assert.Equal(GalleryLayoutMode.Waterfall, reloaded.GetGalleryLayout("external:synthetic").ToOptions().Mode);
            Assert.Equal(22, reloaded.GetGalleryLayout("external:synthetic").Spacing);
            Assert.Equal(400, reloaded.GetGalleryLayout("external:synthetic").TargetSize);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
