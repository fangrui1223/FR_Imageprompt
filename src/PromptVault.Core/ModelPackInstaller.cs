using System.IO.Compression;

namespace PromptVault.Core;

public sealed class ModelPackInstaller
{
    private readonly HttpClient _httpClient;
    public ModelPackInstaller(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();

    public async Task InstallFromUrlAsync(Uri uri, string expectedSha256, string modelsDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modelsDirectory);
        var download = Path.Combine(modelsDirectory, $".model-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 256, true);
            var buffer = new byte[1024 * 256];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                if (length is > 0) progress?.Report(total / (double)length.Value);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await VerifyAndInstallAsync(download, expectedSha256, modelsDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally { TryDelete(download); }
    }

    public async Task VerifyAndInstallAsync(string zipPath, string expectedSha256, string modelsDirectory, CancellationToken cancellationToken = default)
    {
        await using (var stream = File.OpenRead(zipPath))
        {
            var actual = await ContentHasher.Sha256Async(stream, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模型包校验失败。");
        }

        var staging = Path.Combine(modelsDirectory, $".install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                var prefix = staging.EndsWith(Path.DirectorySeparatorChar) ? staging : staging + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模型包包含不安全路径。");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }

            var clip = Path.Combine(staging, "clip");
            if (!File.Exists(Path.Combine(clip, "image_encoder.onnx")) || !File.Exists(Path.Combine(clip, "manifest.json")))
                throw new InvalidDataException("模型包缺少 image_encoder.onnx 或 manifest.json。");
            var target = Path.Combine(modelsDirectory, "clip");
            if (Directory.Exists(target)) Directory.Move(target, target + $".old-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.Move(clip, target);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
