using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;

namespace PromptVault.PerformanceGate;

internal static class Program
{
    private const int LargeWidth = 3840;
    private const int LargeHeight = 2160;
    private const int LargeDecodeWidth = 2800;
    private const int WorkingCopyCount = 32;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        var options = ProbeOptions.Parse(args);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var workingRoot = Path.Combine(
            Path.GetTempPath(),
            "PromptVaultPerformanceGate",
            Guid.NewGuid().ToString("N"));
        var fixtureRoot = options.FixtureOutput is null
            ? workingRoot
            : Path.GetFullPath(options.FixtureOutput);
        Directory.CreateDirectory(workingRoot);
        Directory.CreateDirectory(fixtureRoot);

        try
        {
            var largePath = Path.Combine(fixtureRoot, "representative-5mb-4k.jpg");
            var uiThumbnailPath = Path.Combine(fixtureRoot, "ui-thumbnail-800x600.jpg");
            var largeFixture = CreateRepresentativeJpeg(
                largePath,
                LargeWidth,
                LargeHeight,
                targetBytes: 5L * 1024 * 1024);
            CreateJpeg(uiThumbnailPath, 800, 600, quality: 84, seed: 20260725);

            var copies = Enumerable.Range(0, WorkingCopyCount)
                .Select(index => Path.Combine(workingRoot, $"large-{index:D2}.jpg"))
                .ToArray();
            foreach (var copy in copies) File.Copy(largePath, copy, overwrite: true);

            var immersiveCache = new ImmersiveImageCache(ImmersiveImageCache.DefaultMemoryBudgetBytes);
            var firstLargeClock = Stopwatch.StartNew();
            var firstLarge = await immersiveCache.LoadAsync(copies[0], LargeDecodeWidth);
            firstLargeClock.Stop();
            var cachedClock = Stopwatch.StartNew();
            var cachedLarge = await immersiveCache.LoadAsync(copies[0], LargeDecodeWidth);
            cachedClock.Stop();

            var direct480 = MeasureUniqueDecodes(copies.Skip(1).Take(8), 480);
            var direct1600 = MeasureUniqueDecodes(copies.Skip(9).Take(8), 1600);
            var direct2800 = MeasureUniqueDecodes(copies.Skip(17).Take(8), LargeDecodeWidth);

            for (var index = 1; index < 17; index++)
            {
                await immersiveCache.LoadAsync(copies[index], LargeDecodeWidth);
            }
            var immersiveSnapshot = immersiveCache.GetSnapshot();

            ThumbnailSchedulerSnapshot thumbnailSnapshot;
            var thumbnailClock = Stopwatch.StartNew();
            await using (var thumbnailScheduler = new ThumbnailScheduler(
                maximumConcurrency: 4,
                memoryBudgetBytes: ThumbnailCache.DefaultMemoryBudgetBytes))
            {
                await Task.WhenAll(copies.Select(path =>
                    thumbnailScheduler.LoadAsync(path, 480, ThumbnailRequestPriority.Visible)));
                thumbnailClock.Stop();
                thumbnailSnapshot = thumbnailScheduler.GetSnapshot();
            }

            var firstLargeMs = Math.Round(firstLargeClock.Elapsed.TotalMilliseconds, 3);
            var cachedLargeMs = Math.Round(cachedClock.Elapsed.TotalMilliseconds, 3);
            var checks = new
            {
                FirstLargeDecode = new
                {
                    ActualMs = firstLargeMs,
                    LimitMs = 300d,
                    Passed = firstLargeMs <= 300
                },
                CachedLargeSwitch = new
                {
                    ActualMs = cachedLargeMs,
                    LimitMs = 100d,
                    Passed = cachedLargeMs <= 100 && cachedLarge.CacheHit
                },
                ThumbnailMemory = new
                {
                    ActualBytes = thumbnailSnapshot.CachedBytes,
                    LimitBytes = 512L * 1024 * 1024,
                    Passed = thumbnailSnapshot.CachedBytes <= 512L * 1024 * 1024
                },
                ImmersiveMemory = new
                {
                    ActualBytes = immersiveSnapshot.CachedBytes,
                    LimitBytes = ImmersiveImageCache.DefaultMemoryBudgetBytes,
                    Passed = immersiveSnapshot.CachedBytes <= ImmersiveImageCache.DefaultMemoryBudgetBytes
                }
            };
            var passed = checks.FirstLargeDecode.Passed
                && checks.CachedLargeSwitch.Passed
                && checks.ThumbnailMemory.Passed
                && checks.ImmersiveMemory.Passed;
            var report = new
            {
                Milestone = "M1-07",
                StartedAtUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = Environment.OSVersion.VersionString,
                    DotNet = Environment.Version.ToString(),
                    LogicalProcessors = Environment.ProcessorCount,
                    AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
                },
                Fixture = new
                {
                    Width = LargeWidth,
                    Height = LargeHeight,
                    largeFixture.Quality,
                    largeFixture.Bytes,
                    TargetBytes = 5L * 1024 * 1024,
                    WorkingCopyCount,
                    UiThumbnailBytes = new FileInfo(uiThumbnailPath).Length,
                    RetainedFixtureDirectory = options.FixtureOutput is null ? null : fixtureRoot
                },
                LargeImage = new
                {
                    DecodeWidth = LargeDecodeWidth,
                    FirstDecodeMs = firstLargeMs,
                    FirstDecodeCacheHit = firstLarge.CacheHit,
                    CachedSwitchMs = cachedLargeMs,
                    CachedSwitchCacheHit = cachedLarge.CacheHit,
                    Unique480 = direct480,
                    Unique1600 = direct1600,
                    Unique2800 = direct2800,
                    ImmersiveCache = new
                    {
                        immersiveSnapshot.RequestCount,
                        immersiveSnapshot.CacheHitCount,
                        immersiveSnapshot.DecodeCount,
                        immersiveSnapshot.CoalescedRequestCount,
                        immersiveSnapshot.EntryCount,
                        immersiveSnapshot.CachedBytes,
                        immersiveSnapshot.MemoryBudgetBytes
                    }
                },
                ThumbnailScheduler = new
                {
                    TotalMs = Math.Round(thumbnailClock.Elapsed.TotalMilliseconds, 3),
                    thumbnailSnapshot.RequestCount,
                    thumbnailSnapshot.CacheHitCount,
                    thumbnailSnapshot.DecodeCount,
                    thumbnailSnapshot.CoalescedRequestCount,
                    thumbnailSnapshot.CanceledBeforeStartCount,
                    thumbnailSnapshot.MaximumObservedConcurrency,
                    thumbnailSnapshot.CacheEntryCount,
                    thumbnailSnapshot.CachedBytes,
                    thumbnailSnapshot.MemoryBudgetBytes
                },
                Checks = checks,
                Passed = passed,
                Caveat = "The representative 5 MB files are independent copies generated on the same local volume. The probe measures real WPF JPEG decode and cache behavior, but does not forcibly purge the Windows filesystem cache."
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Report: {outputPath}");
            return passed ? 0 : 2;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workingRoot)) Directory.Delete(workingRoot, recursive: true);
            }
            catch (IOException)
            {
                Console.Error.WriteLine($"Temporary performance data remains at: {workingRoot}");
            }
        }
    }

    private static DecodeDistribution MeasureUniqueDecodes(IEnumerable<string> paths, int decodeWidth)
    {
        var samples = new List<double>();
        foreach (var path in paths)
        {
            var clock = Stopwatch.StartNew();
            var image = ImagePipeline.LoadPreview(path, decodeWidth);
            clock.Stop();
            GC.KeepAlive(image);
            samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return new DecodeDistribution(
            samples.Count,
            Percentile(samples, 0.5),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            samples.Count == 0 ? 0 : Math.Round(samples[^1], 3));
    }

    private static RepresentativeJpeg CreateRepresentativeJpeg(
        string path,
        int width,
        int height,
        long targetBytes)
    {
        var source = CreateNoiseBitmap(width, height, seed: 20260724);
        byte[]? selected = null;
        var selectedQuality = 0;
        var selectedDistance = long.MaxValue;
        foreach (var quality in new[] { 68, 72, 76, 80, 84, 88, 92 })
        {
            var bytes = EncodeJpeg(source, quality);
            var distance = Math.Abs(bytes.LongLength - targetBytes);
            if (distance >= selectedDistance) continue;
            selected = bytes;
            selectedQuality = quality;
            selectedDistance = distance;
        }

        File.WriteAllBytes(path, selected!);
        return new RepresentativeJpeg(selectedQuality, selected!.LongLength);
    }

    private static void CreateJpeg(
        string path,
        int width,
        int height,
        int quality,
        int seed)
    {
        File.WriteAllBytes(path, EncodeJpeg(CreateNoiseBitmap(width, height, seed), quality));
    }

    private static BitmapSource CreateNoiseBitmap(int width, int height, int seed)
    {
        const int bytesPerPixel = 3;
        var stride = checked(width * bytesPerPixel);
        var pixels = new byte[checked(stride * height)];
        new Random(seed).NextBytes(pixels);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0) return 0;
        var index = Math.Clamp(
            (int)Math.Ceiling(ordered.Count * percentile) - 1,
            0,
            ordered.Count - 1);
        return Math.Round(ordered[index], 3);
    }

    private sealed record RepresentativeJpeg(int Quality, long Bytes);
    private sealed record DecodeDistribution(
        int Count,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaximumMs);

    private sealed record ProbeOptions(string OutputPath, string? FixtureOutput)
    {
        public static ProbeOptions Parse(IReadOnlyList<string> args)
        {
            var output = "docs/performance/m1-07-media-gate.json";
            string? fixtureOutput = null;
            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--output":
                        output = ReadValue(args, ref index);
                        break;
                    case "--fixture-output":
                        fixtureOutput = ReadValue(args, ref index);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {args[index]}");
                }
            }
            return new ProbeOptions(output, fixtureOutput);
        }

        private static string ReadValue(IReadOnlyList<string> args, ref int index)
        {
            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("Missing option value.");
            }
            return args[index];
        }
    }
}
