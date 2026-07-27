using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PromptVault.Core;

namespace PromptVault.App.Services;

internal sealed record DiagnosticBundleResult(
    string ZipPath,
    long SizeBytes,
    int LogFileCount,
    IReadOnlyList<string> Entries);

internal static class DiagnosticBundleExporter
{
    public static async Task<DiagnosticBundleResult> ExportAsync(
        string zipPath,
        LibraryPaths paths,
        CancellationToken cancellationToken = default)
    {
        zipPath = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        var entries = new List<string>();
        var logCount = 0;
        await using (var stream = new FileStream(
            zipPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            useAsync: true))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifestEntry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
            await using (var output = manifestEntry.Open())
            {
                var manifest = CreateManifest(paths);
                await JsonSerializer.SerializeAsync(
                    output,
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken).ConfigureAwait(false);
            }
            entries.Add(manifestEntry.FullName);

            if (Directory.Exists(AppLog.LogDirectoryPath))
            {
                foreach (var logPath in Directory.EnumerateFiles(AppLog.LogDirectoryPath, "app-*.jsonl")
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(
                        $"logs/{Path.GetFileName(logPath)}",
                        CompressionLevel.Optimal);
                    await using var output = entry.Open();
                    await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                    using var reader = new StreamReader(logPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                    {
                        await writer.WriteLineAsync(
                            DiagnosticRedactor.RedactJsonLine(line).AsMemory(),
                            cancellationToken).ConfigureAwait(false);
                    }
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    entries.Add(entry.FullName);
                    logCount++;
                }
            }

            if (File.Exists(paths.UpgradeState))
            {
                var entry = archive.CreateEntry("upgrade-state.json", CompressionLevel.Optimal);
                await using var output = entry.Open();
                await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                var redacted = DiagnosticRedactor.RedactJsonLine(
                    await File.ReadAllTextAsync(paths.UpgradeState, cancellationToken).ConfigureAwait(false));
                await writer.WriteAsync(redacted.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                entries.Add(entry.FullName);
            }
        }

        return new DiagnosticBundleResult(
            zipPath,
            new FileInfo(zipPath).Length,
            logCount,
            entries);
    }

    private static object CreateManifest(LibraryPaths paths)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(DiagnosticBundleExporter).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        return new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            application = "FR_Imageprompt",
            version,
            databaseBytes = File.Exists(paths.Database) ? new FileInfo(paths.Database).Length : 0,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            culture = CultureInfo.CurrentCulture.Name,
            uiCulture = CultureInfo.CurrentUICulture.Name,
            redaction = new
            {
                prompts = "removed",
                images = "not collected",
                credentials = "removed",
                paths = "reduced to redacted file names",
                settings = "not collected",
                environment = "whitelist only"
            }
        };
    }
}
