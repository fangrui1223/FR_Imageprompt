using System.IO.Compression;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class ModelPackInstallerTests
{
    [Fact]
    public async Task InstallsValidatedOfflinePack()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultModelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "model.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "clip/image_encoder.onnx", "model");
            Write(archive, "clip/manifest.json", "{}");
        }
        string hash;
        await using (var stream = File.OpenRead(zip)) hash = await ContentHasher.Sha256Async(stream);
        await new ModelPackInstaller().VerifyAndInstallAsync(zip, hash, Path.Combine(root, "models"));
        Assert.True(File.Exists(Path.Combine(root, "models", "clip", "manifest.json")));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task RejectsWrongHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultModelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "model.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) Write(archive, "clip/manifest.json", "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModelPackInstaller().VerifyAndInstallAsync(zip, new string('0', 64), Path.Combine(root, "models")));
        Directory.Delete(root, true);
    }

    [Fact]
    public void OldModelBackupRetentionIsBoundedAndConfigurable()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultModelRetentionTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (var suffix in new[] { "20260101000000000", "20260201000000000", "20260301000000000", "20260401000000000" })
            {
                Directory.CreateDirectory(Path.Combine(root, $"clip.old-{suffix}"));
            }

            Assert.Equal(2, ModelPackInstaller.PruneOldBackups(root, retainedBackups: 2));
            Assert.Equal(
                ["clip.old-20260401000000000", "clip.old-20260301000000000"],
                Directory.EnumerateDirectories(root, "clip.old-*")
                    .Select(path => Path.GetFileName(path)!)
                    .OrderDescending(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            Assert.Equal(2, ModelPackInstaller.PruneOldBackups(root, retainedBackups: 0));
            Assert.Empty(Directory.EnumerateDirectories(root, "clip.old-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(ZipArchive archive, string path, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(value);
    }
}
