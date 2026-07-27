using System.IO.Compression;
using System.Text;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class DiagnosticBundleTests
{
    [Fact]
    public async Task ExportContainsOnlyRedactedLogsAndWhitelistedDiagnostics()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PromptVaultDiagnosticTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LibraryPaths(Path.Combine(root, "library"));
            paths.EnsureCreated();
            var imagePath = Path.Combine(paths.Originals, "private-image.png");
            File.WriteAllText(imagePath, "USER_IMAGE_BYTES_MUST_NOT_BE_EXPORTED");
            var secretPrompt = "a private full prompt that must not appear";
            var secretKey = "sk-test-secret-key";
            AppLog.Warning(
                "diagnostic-redaction-test",
                $@"api_key={secretKey} at C:\Users\Private\settings.json",
                data: new
                {
                    prompt = secretPrompt,
                    imagePath,
                    apiKey = secretKey,
                    safeCount = 7
                });

            var zipPath = Path.Combine(root, "diagnostics.zip");
            var result = await DiagnosticBundleExporter.ExportAsync(zipPath, paths);

            Assert.True(File.Exists(result.ZipPath));
            Assert.Contains("diagnostics.json", result.Entries);
            Assert.DoesNotContain(result.Entries, entry =>
                entry.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase));
            using var archive = ZipFile.OpenRead(zipPath);
            var text = new StringBuilder();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                text.Append(await reader.ReadToEndAsync());
            }
            var content = text.ToString();
            Assert.DoesNotContain(secretPrompt, content, StringComparison.Ordinal);
            Assert.DoesNotContain(secretKey, content, StringComparison.Ordinal);
            Assert.DoesNotContain(imagePath, content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("USER_IMAGE_BYTES_MUST_NOT_BE_EXPORTED", content, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
            Assert.Contains("\"safeCount\":7", content, StringComparison.Ordinal);
            Assert.Contains("\"environment\": \"whitelist only\"", content, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void RedactorRemovesCredentialsAndFullWindowsPaths()
    {
        var redacted = DiagnosticRedactor.RedactText(
            @"Authorization: Bearer abc.def.ghi; token=top-secret; C:\Users\Name\Pictures\private.png");

        Assert.DoesNotContain("abc.def.ghi", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Name", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("private.png", redacted, StringComparison.Ordinal);
    }
}
