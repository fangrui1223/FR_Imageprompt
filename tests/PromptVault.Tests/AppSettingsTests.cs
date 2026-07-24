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
}
