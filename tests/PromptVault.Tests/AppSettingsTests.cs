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
            settings.Save();

            var reloaded = AppSettings.Load(path);

            Assert.True(reloaded.ReducedMotionEnabled);
            Assert.False(reloaded.CaptureListeningEnabled);
            Assert.Equal("D:\\SyntheticLibrary", reloaded.LibraryRoot);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
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
}
