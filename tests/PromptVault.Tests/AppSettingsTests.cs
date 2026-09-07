using PromptVault.App;
using PromptVault.App.Services;
using PromptVault.Core;

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
            settings.BoardAlwaysOnTop = true;
            settings.Save();

            var reloaded = AppSettings.Load(path);

            Assert.True(reloaded.ReducedMotionEnabled);
            Assert.True(reloaded.InspectorPinned);
            Assert.Equal(EdgeIntentProfile.HighSensitivity, reloaded.EdgeMenuSensitivity);
            Assert.True(reloaded.EdgeMenusAlwaysVisible);
            Assert.True(reloaded.BoardAlwaysOnTop);
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

    [Fact]
    public void M7InteractionPreferencesRoundTripAndClampSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            var settings = AppSettings.Load(path);
            settings.CaptureQuickEditEnabled = true;
            settings.InspectorWidth = 9999;
            settings.GalleryAppearance.HorizontalSpacing = 7;
            settings.GalleryAppearance.VerticalSpacing = 11;
            settings.GalleryAppearance.CornerRadius = 12;
            settings.GalleryAppearance.BorderThickness = 1.5;
            settings.GalleryAppearance.BorderColor = "#AABBCC";
            settings.Save();

            var reloaded = AppSettings.Load(path);

            Assert.True(reloaded.CaptureQuickEditEnabled);
            Assert.Equal(720, reloaded.InspectorWidth);
            Assert.Equal(7, reloaded.GalleryAppearance.HorizontalSpacing);
            Assert.Equal(11, reloaded.GalleryAppearance.VerticalSpacing);
            Assert.Equal(12, reloaded.GalleryAppearance.CornerRadius);
            Assert.Equal(1.5, reloaded.GalleryAppearance.BorderThickness);
            Assert.Equal("#AABBCC", reloaded.GalleryAppearance.BorderColor);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void LegacySingleSpacingMigratesToBothAppearanceAxes()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                path,
                """
                {
                  "LibraryRoot": "",
                  "GalleryLayouts": {
                    "library": {
                      "Mode": "Waterfall",
                      "Density": "Custom",
                      "Spacing": 22,
                      "TargetSize": 320
                    }
                  }
                }
                """);

            var settings = AppSettings.Load(path);

            Assert.Equal(22, settings.GalleryAppearance.HorizontalSpacing);
            Assert.Equal(22, settings.GalleryAppearance.VerticalSpacing);
            Assert.Equal(12, settings.GalleryAppearance.CornerRadius);
            Assert.Equal(0, settings.GalleryAppearance.BorderThickness);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void SettingsWithoutLegacyLayoutUseRecommendedAppearanceDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, """{ "LibraryRoot": "", "GalleryLayouts": {} }""");

            var settings = AppSettings.Load(path);

            Assert.Equal(8, settings.GalleryAppearance.HorizontalSpacing);
            Assert.Equal(8, settings.GalleryAppearance.VerticalSpacing);
            Assert.Equal(12, settings.GalleryAppearance.CornerRadius);
            Assert.Equal(0, settings.GalleryAppearance.BorderThickness);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(12, 0, 12)]
    [InlineData(32, 0, 32)]
    [InlineData(32, 4, 28)]
    [InlineData(2, 4, 0)]
    public void GalleryImageCornerRadiusFollowsCardRadiusInsideBorder(
        double cornerRadius,
        double borderThickness,
        double expected)
    {
        var actual = GalleryAppearanceGeometry.CalculateImageCornerRadius(
            cornerRadius,
            borderThickness);

        Assert.Equal(expected, actual.TopLeft);
        Assert.Equal(expected, actual.TopRight);
        Assert.Equal(expected, actual.BottomRight);
        Assert.Equal(expected, actual.BottomLeft);
    }

    [Fact]
    public void BoardNotePresetsRoundTripAsFiveGlobalStyleOnlySlots()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            var settings = AppSettings.Load(path);
            settings.BoardNotePresets[2] = settings.BoardNotePresets[2] with
            {
                FontSize = 83,
                Alignment = BoardNoteTextAlignment.Right,
                BackgroundEnabled = false
            };
            settings.Save();

            var reloaded = AppSettings.Load(path);

            Assert.Equal(5, reloaded.BoardNotePresets.Count);
            Assert.Equal(83, reloaded.BoardNotePresets[2].FontSize);
            Assert.Equal(BoardNoteTextAlignment.Right, reloaded.BoardNotePresets[2].Alignment);
            Assert.False(reloaded.BoardNotePresets[2].BackgroundEnabled);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void MissingAndDamagedPresetSlotsRecoverIndividually()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                path,
                """
                {
                  "LibraryRoot": "",
                  "BoardNotePresets": [
                    {
                      "FontSize": 999,
                      "LineSpacing": -5,
                      "Alignment": 0,
                      "TextColor": "bad",
                      "BackgroundEnabled": true,
                      "BackgroundColor": "bad",
                      "BackgroundOpacity": 9,
                      "CornerRadius": -1,
                      "VerticalPadding": 999
                    }
                  ]
                }
                """);

            var settings = AppSettings.Load(path);

            Assert.Equal(5, settings.BoardNotePresets.Count);
            Assert.Equal(300, settings.BoardNotePresets[0].FontSize);
            Assert.Equal(1, settings.BoardNotePresets[0].LineSpacing);
            Assert.Equal(BoardNoteStyle.Default.TextColor, settings.BoardNotePresets[0].TextColor);
            Assert.Equal(BoardNotePresetDefaults.Create()[1], settings.BoardNotePresets[1]);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
