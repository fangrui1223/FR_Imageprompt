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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadClosesWriterAndFailedValidationPreservesInstalledModel(bool wrongHash)
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultModelDownloadTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "clip"));
            var model = Path.Combine(root, "clip", "image_encoder.onnx");
            await File.WriteAllTextAsync(model, "previous");
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, "clip/image_encoder.onnx", "replacement");
                Write(archive, "clip/manifest.json", "{}");
            }
            var payload = buffer.ToArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
            using var http = new System.Net.Http.HttpClient(new SyntheticHttpHandler(payload));
            var installer = new ModelPackInstaller(http);
            var install = installer.InstallFromUrlAsync(new Uri("https://synthetic.invalid/model.zip"), wrongHash ? new string('0', 64) : hash, root);
            if (wrongHash) await Assert.ThrowsAsync<InvalidDataException>(() => install);
            else await install;
            Assert.Equal(wrongHash ? "previous" : "replacement", await File.ReadAllTextAsync(model));
            Assert.Empty(Directory.EnumerateFiles(root, ".model-*"));
            Assert.Empty(Directory.EnumerateDirectories(root, ".install-*"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallFromUrlAsync(new Uri("https://synthetic.invalid/model.zip"), hash, root, cancellationToken: cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(root, ".model-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Write(ZipArchive archive, string path, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(value);
    }
}
