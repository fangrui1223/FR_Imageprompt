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

    private static void Write(ZipArchive archive, string path, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(value);
    }
}
