using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.ExternalIndexProbe;

internal static class Program
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Wl1sAAAAASUVORK5CYII=");

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        var options = ParseOptions(args);
        var probeRoot = Path.Combine(
            Path.GetTempPath(),
            "PromptVaultExternalIndexProbe",
            Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(probeRoot, "library");
        var externalRoot = Path.Combine(probeRoot, "external");
        Directory.CreateDirectory(externalRoot);
        try
        {
            var setupClock = Stopwatch.StartNew();
            Parallel.For(
                0,
                options.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
                index =>
                {
                    var path = Path.Combine(externalRoot, $"image-{index:D5}.png");
                    File.WriteAllBytes(path, OnePixelPng);
                    File.SetLastWriteTimeUtc(
                        path,
                        DateTime.UtcNow.AddSeconds(-(index % 10_000)));
                });
            setupClock.Stop();

            var repository = new LibraryRepository(new LibraryPaths(libraryRoot));
            await repository.InitializeAsync();
            var reader = new CountingMetadataReader(new WpfExternalImageMetadataReader());
            using var service = new ExternalFolderIndexService(
                repository,
                reader,
                validationInterval: TimeSpan.FromHours(6),
                watcherDebounce: TimeSpan.FromMilliseconds(20));
            var folder = new ExternalFolderSetting
            {
                Id = "probe-folder",
                Name = "probe",
                Path = externalRoot,
                AddedAt = DateTimeOffset.UtcNow
            };

            var firstScanClock = Stopwatch.StartNew();
            var initialState = await service.EnsureFolderIndexedAsync(folder, forceValidation: true);
            firstScanClock.Stop();
            var initialMetadataReads = reader.ReadCount;

            var querySamples = new List<double>();
            ExternalFileSearchPage? firstPage = null;
            for (var iteration = 0; iteration < options.QueryIterations; iteration++)
            {
                var clock = Stopwatch.StartNew();
                var page = await repository.SearchExternalFilesAsync(
                    folder.Id,
                    iteration % 2 == 0 ? "" : "image-29",
                    GallerySortOrder.NewestFirst,
                    240);
                clock.Stop();
                if (iteration > 0) querySamples.Add(clock.Elapsed.TotalMilliseconds);
                firstPage ??= page;
            }

            var unchangedClock = Stopwatch.StartNew();
            var unchangedState = await service.EnsureFolderIndexedAsync(folder, forceValidation: true);
            unchangedClock.Stop();
            var unchangedMetadataReads = reader.ReadCount - initialMetadataReads;

            var addedPath = Path.Combine(externalRoot, "image-new.png");
            File.WriteAllBytes(addedPath, OnePixelPng);
            var addClock = Stopwatch.StartNew();
            await service.ProcessFileChangeAsync(
                folder.Id,
                ExternalFolderIndexService.ExternalFileChangeKind.Changed,
                addedPath);
            addClock.Stop();

            var renamedPath = Path.Combine(externalRoot, "image-renamed.png");
            File.Move(addedPath, renamedPath);
            var renameClock = Stopwatch.StartNew();
            await service.ProcessFileChangeAsync(
                folder.Id,
                ExternalFolderIndexService.ExternalFileChangeKind.Renamed,
                renamedPath,
                addedPath);
            renameClock.Stop();

            File.Delete(renamedPath);
            var deleteClock = Stopwatch.StartNew();
            await service.ProcessFileChangeAsync(
                folder.Id,
                ExternalFolderIndexService.ExternalFileChangeKind.Deleted,
                renamedPath);
            deleteClock.Stop();
            var finalState = await service.GetStateAsync(folder.Id);
            var databaseBytes = new FileInfo(repository.Paths.Database).Length;
            querySamples.Sort();

            var report = new
            {
                Milestone = "M1-06",
                StartedAtUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = Environment.OSVersion.ToString(),
                    DotNet = Environment.Version.ToString(),
                    ProcessorCount = Environment.ProcessorCount,
                    ItemCount = options.Count,
                    QueryIterations = options.QueryIterations,
                    SourceFormat = "valid 1x1 PNG used to isolate filesystem, header metadata, and SQLite index cost",
                    SourceBytesPerFile = OnePixelPng.Length,
                    ValidationIntervalHours = 6,
                    PageSize = 240
                },
                Setup = new
                {
                    FileCreationMs = Round(setupClock.Elapsed.TotalMilliseconds)
                },
                InitialScan = new
                {
                    ElapsedMs = Round(firstScanClock.Elapsed.TotalMilliseconds),
                    MetadataReads = initialMetadataReads,
                    AvailableFiles = initialState?.AvailableFiles ?? 0,
                    FailedFiles = initialState?.FailedFiles ?? 0,
                    Status = initialState?.Status.ToString()
                },
                UnchangedValidation = new
                {
                    ElapsedMs = Round(unchangedClock.Elapsed.TotalMilliseconds),
                    MetadataReads = unchangedMetadataReads,
                    AvailableFiles = unchangedState?.AvailableFiles ?? 0,
                    FailedFiles = unchangedState?.FailedFiles ?? 0
                },
                IndexedQuery = new
                {
                    SampleCount = querySamples.Count,
                    P50Ms = Percentile(querySamples, 0.50),
                    P95Ms = Percentile(querySamples, 0.95),
                    MaximumMs = querySamples.Count == 0 ? 0 : Round(querySamples[^1]),
                    FirstPageItems = firstPage?.Items.Count ?? 0,
                    TotalCount = firstPage?.TotalCount ?? 0,
                    HasStableCursor = firstPage?.NextCursor is not null
                },
                IncrementalChanges = new
                {
                    AddMs = Round(addClock.Elapsed.TotalMilliseconds),
                    RenameMs = Round(renameClock.Elapsed.TotalMilliseconds),
                    DeleteMs = Round(deleteClock.Elapsed.TotalMilliseconds),
                    MissingFilesAfterDelete = finalState?.MissingFiles ?? 0
                },
                Storage = new
                {
                    DatabaseBytes = databaseBytes
                },
                Safety = new
                {
                    TestRoot = probeRoot,
                    UsesTemporaryDirectory = true,
                    DeletesTestRootOnExit = true,
                    OpensUserLibrary = false
                }
            };

            var json = JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
            File.WriteAllText(options.Output, json);
            Console.WriteLine(json);
            return initialState?.AvailableFiles == options.Count
                && initialState.FailedFiles == 0
                && unchangedMetadataReads == 0
                ? 0
                : 2;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(probeRoot)) Directory.Delete(probeRoot, true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Probe cleanup failed: {ex.Message}");
            }
        }
    }

    private static ProbeOptions ParseOptions(IReadOnlyList<string> args)
    {
        var count = 30_000;
        var iterations = 12;
        var output = Path.GetFullPath(
            Path.Combine("docs", "performance", "external-index-probe.json"));
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--count":
                    count = int.Parse(args[++index]);
                    break;
                case "--iterations":
                    iterations = int.Parse(args[++index]);
                    break;
                case "--output":
                    output = Path.GetFullPath(args[++index]);
                    break;
            }
        }
        return new ProbeOptions(
            Math.Clamp(count, 1, 100_000),
            Math.Clamp(iterations, 2, 100),
            output);
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0) return 0;
        var index = Math.Clamp(
            (int)Math.Ceiling(ordered.Count * percentile) - 1,
            0,
            ordered.Count - 1);
        return Round(ordered[index]);
    }

    private static double Round(double value) => Math.Round(value, 3);

    private sealed record ProbeOptions(int Count, int QueryIterations, string Output);

    private sealed class CountingMetadataReader(IExternalImageMetadataReader inner)
        : IExternalImageMetadataReader
    {
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);

        public ExternalImageMetadata Read(string path)
        {
            Interlocked.Increment(ref _readCount);
            return inner.Read(path);
        }
    }
}
