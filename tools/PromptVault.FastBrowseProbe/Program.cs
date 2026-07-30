using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.FastBrowseProbe;

internal static class Program
{
    private const int VisibleCount = 24;
    private const int TypicalAvailableMemoryGiB = 16;
    private static readonly int[] ReadinessTimesMs = [0, 50, 100, 150, 250, 500];
    private static readonly double[] JumpPositions = [0, 0.25, 0.5, 0.75, 1, 0.75, 0.5, 0.25, 0];
    private static readonly (int WidthUnits, int HeightUnits)[] AspectUnits =
    [
        (1, 4),
        (1, 2),
        (2, 3),
        (1, 1),
        (3, 2),
        (2, 1),
        (4, 1)
    ];

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        var options = ProbeOptions.Parse(args);
        var startedAt = DateTimeOffset.UtcNow;
        var workingRoot = Path.Combine(
            Path.GetTempPath(),
            "PromptVaultFastBrowseProbe",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingRoot);

        try
        {
            var small = await RunScaleAsync(
                Path.Combine(workingRoot, "small"),
                550,
                64L * 1024 * 1024 * 1024);
            ForceCollection();
            var medium = await RunScaleAsync(
                Path.Combine(workingRoot, "medium"),
                5_000,
                TypicalAvailableMemoryGiB * 1024L * 1024 * 1024);
            ForceCollection();
            var large = await RunScaleAsync(
                Path.Combine(workingRoot, "large"),
                30_000,
                TypicalAvailableMemoryGiB * 1024L * 1024 * 1024);
            var virtualization = ReadVirtualizationGate(options.VirtualizationPath);
            var checks = CreateChecks(small, medium, large, virtualization);
            UiFixtureResult? uiFixture = null;
            if (!string.IsNullOrWhiteSpace(options.UiLibraryParent))
            {
                uiFixture = await CreateUiFixtureAsync(options.UiLibraryParent);
            }

            var report = new
            {
                Milestone = "M7.2-adaptive-fast-browsing",
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = Environment.OSVersion.VersionString,
                    DotNet = Environment.Version.ToString(),
                    LogicalProcessors = Environment.ProcessorCount,
                    ActualAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    TypicalPolicyMemoryBytes = TypicalAvailableMemoryGiB * 1024L * 1024 * 1024
                },
                FixtureContract = new
                {
                    UniquePathPerItem = true,
                    UniqueContentTokenPerItem = true,
                    AspectRatios = new[] { "1:4", "1:2", "2:3", "1:1", "3:2", "2:1", "4:1" },
                    VisibleItemsPerJump = VisibleCount,
                    JumpPercentages = JumpPositions.Select(value => value * 100).ToArray(),
                    ReadinessSampleMilliseconds = ReadinessTimesMs,
                    ContainsUserData = false
                },
                Scales = new { Small550 = small, Medium5000 = medium, Large30000 = large },
                Virtualization = virtualization,
                Checks = checks,
                UiFixture = uiFixture is null
                    ? null
                    : new
                    {
                        uiFixture.ItemCount,
                        uiFixture.UniqueImageCount,
                        uiFixture.CaptureListeningEnabled,
                        uiFixture.OnlineAiEnabled,
                        uiFixture.RelativeSettingsHint
                    },
                DataSafety = new
                {
                    SyntheticFixturesOnly = true,
                    RealLibraryOpened = false,
                    RealLibraryModified = false,
                    ClipboardMonitoringUsed = false,
                    OnlineRequests = 0,
                    ApiKeysUsed = 0,
                    AbsolutePathsSerialized = false
                },
                Passed = checks.All(check => check.Passed)
            };

            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (ContainsAbsoluteWindowsPath(json))
            {
                throw new InvalidDataException("The performance report contains an absolute Windows path.");
            }
            await File.WriteAllTextAsync(options.OutputPath, json, new UTF8Encoding(false));
            Console.WriteLine(json);
            Console.WriteLine($"Report: {options.OutputPath}");
            if (uiFixture is not null)
            {
                Console.WriteLine($"UI settings: {uiFixture.SettingsPath}");
            }
            return checks.All(check => check.Passed) ? 0 : 1;
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    private static async Task<ScaleReport> RunScaleAsync(
        string root,
        int count,
        long policyAvailableMemoryBytes)
    {
        var plan = AdaptiveFastBrowsePolicy.Create(count, policyAvailableMemoryBytes);
        var creationClock = Stopwatch.StartNew();
        var paths = CreateFixtures(root, count, plan.MotionPixels);
        creationClock.Stop();
        var metadata = MeasureMetadata(count);
        var warmDurations = new List<double>();
        var jumps = new List<JumpReport>();
        ForegroundPreemptionReport preemption;
        CancellationReport cancellation;
        HighQualityUpgradeReport highQuality;

        await using (var motion = new ThumbnailScheduler(
                         plan.MotionDecodeConcurrency,
                         plan.MotionMemoryBudgetBytes))
        {
            var initialOrder = FastBrowsePrefetchPlanner.CreateBackgroundWarmOrder(
                count,
                0,
                plan,
                GalleryScrollDirection.Forward);
            await WarmInBatchesAsync(
                motion,
                paths,
                initialOrder,
                plan,
                warmDurations,
                CancellationToken.None);

            var previousFirst = 0;
            foreach (var percentage in JumpPositions)
            {
                var first = (int)Math.Round(
                    (count - VisibleCount) * percentage,
                    MidpointRounding.AwayFromZero);
                var direction = first > previousFirst
                    ? GalleryScrollDirection.Forward
                    : first < previousFirst
                        ? GalleryScrollDirection.Backward
                        : GalleryScrollDirection.None;
                var selected = paths.Skip(first).Take(VisibleCount).ToArray();
                jumps.Add(await MeasureVisibleReadinessAsync(
                    motion,
                    selected,
                    plan.MotionPixels,
                    percentage,
                    first));

                if (plan.Tier == AdaptiveGalleryTier.LargeRollingWindow)
                {
                    var rollingOrder = FastBrowsePrefetchPlanner.CreateOrderedWindow(
                        count,
                        first + VisibleCount / 2,
                        plan.WarmItemLimit,
                        direction);
                    await WarmInBatchesAsync(
                        motion,
                        paths,
                        rollingOrder,
                        plan,
                        warmDurations,
                        CancellationToken.None);
                }
                previousFirst = first;
            }

            cancellation = await MeasureCancellationAsync(paths, plan);
            preemption = await MeasureForegroundPreemptionAsync(paths, plan);
            highQuality = await MeasureHighQualityUpgradeAsync(motion, paths, plan);
            var snapshot = motion.GetSnapshot();
            var expectedWarmRequests = plan.Tier == AdaptiveGalleryTier.LargeRollingWindow
                ? plan.WarmItemLimit * (1 + JumpPositions.Length)
                : count;
            return new ScaleReport(
                count,
                plan.Tier.ToString(),
                plan.MotionPixels,
                plan.WarmItemLimit,
                plan.BatchSize,
                plan.MotionDecodeConcurrency,
                plan.MotionMemoryBudgetBytes,
                Math.Round(creationClock.Elapsed.TotalMilliseconds, 3),
                paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                count,
                metadata,
                expectedWarmRequests,
                Percentiles.From(warmDurations),
                jumps,
                preemption,
                cancellation,
                highQuality,
                snapshot,
                snapshot.CachedBytes <= snapshot.MemoryBudgetBytes);
        }
    }

    private static async Task WarmInBatchesAsync(
        ThumbnailScheduler scheduler,
        IReadOnlyList<string> paths,
        IReadOnlyList<int> order,
        AdaptiveFastBrowsePlan plan,
        List<double> durations,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < order.Count; offset += plan.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = order.Skip(offset).Take(plan.BatchSize).ToArray();
            var tasks = batch.Select(async index =>
            {
                var clock = Stopwatch.StartNew();
                await scheduler.LoadAsync(
                    paths[index],
                    plan.MotionPixels,
                    ThumbnailRequestPriority.Prefetch,
                    cancellationToken);
                clock.Stop();
                return clock.Elapsed.TotalMilliseconds;
            }).ToArray();
            var elapsed = await Task.WhenAll(tasks);
            durations.AddRange(elapsed);
            await Task.Yield();
        }
    }

    private static async Task<JumpReport> MeasureVisibleReadinessAsync(
        ThumbnailScheduler scheduler,
        IReadOnlyList<string> selected,
        int targetPixels,
        double percentage,
        int firstIndex)
    {
        var instantCached = selected.Count(path =>
            scheduler.TryGetCached(path, targetPixels, out _));
        var clock = Stopwatch.StartNew();
        var tasks = selected.Select(path => scheduler.LoadAsync(
            path,
            targetPixels,
            ThumbnailRequestPriority.Visible)).ToArray();
        var allReadyTask = Task.WhenAll(tasks).ContinueWith(
            _ => clock.Elapsed.TotalMilliseconds,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var readiness = new List<ReadinessSample>();
        foreach (var sampleAtMs in ReadinessTimesMs)
        {
            var remaining = sampleAtMs - clock.Elapsed.TotalMilliseconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
            readiness.Add(new ReadinessSample(
                sampleAtMs,
                tasks.Count(task => task.IsCompletedSuccessfully),
                tasks.Length));
        }
        await Task.WhenAll(tasks);
        var allReadyMs = await allReadyTask;
        clock.Stop();
        return new JumpReport(
            percentage * 100,
            firstIndex,
            tasks.Length,
            instantCached,
            Math.Round(allReadyMs, 3),
            readiness);
    }

    private static async Task<ForegroundPreemptionReport> MeasureForegroundPreemptionAsync(
        IReadOnlyList<string> paths,
        AdaptiveFastBrowsePlan plan)
    {
        var memoryBudget = Math.Min(
            plan.MotionMemoryBudgetBytes,
            256L * 1024 * 1024);
        await using var scheduler = new ThumbnailScheduler(
            plan.MotionDecodeConcurrency,
            memoryBudget);
        var prefetchPaths = paths.Take(Math.Min(96, paths.Count)).ToArray();
        var prefetch = prefetchPaths.Select(path => scheduler.LoadAsync(
            path,
            plan.MotionPixels,
            ThumbnailRequestPriority.Prefetch)).ToArray();
        await Task.Delay(2);
        var target = paths[Math.Min(paths.Count - 1, prefetchPaths.Length - 1)];
        var clock = Stopwatch.StartNew();
        await scheduler.LoadAsync(
            target,
            plan.MotionPixels,
            ThumbnailRequestPriority.Visible);
        clock.Stop();
        await Task.WhenAll(prefetch);
        var snapshot = scheduler.GetSnapshot();
        return new ForegroundPreemptionReport(
            Math.Round(clock.Elapsed.TotalMilliseconds, 3),
            snapshot.MaximumObservedConcurrency,
            snapshot.RunningPrefetchCount,
            clock.Elapsed.TotalMilliseconds <= 500);
    }

    private static async Task<CancellationReport> MeasureCancellationAsync(
        IReadOnlyList<string> paths,
        AdaptiveFastBrowsePlan plan)
    {
        await using var scheduler = new ThumbnailScheduler(
            plan.MotionDecodeConcurrency,
            Math.Min(plan.MotionMemoryBudgetBytes, 256L * 1024 * 1024));
        var before = scheduler.GetSnapshot();
        using var cancellation = new CancellationTokenSource();
        var start = Math.Max(0, paths.Count - Math.Min(96, paths.Count));
        var tasks = paths.Skip(start).Select(path => scheduler.LoadAsync(
            path,
            plan.MotionPixels,
            ThumbnailRequestPriority.Prefetch,
            cancellation.Token)).ToArray();
        cancellation.Cancel();
        var canceledSubscribers = 0;
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                canceledSubscribers++;
            }
        }
        var after = scheduler.GetSnapshot();
        return new CancellationReport(
            tasks.Length,
            canceledSubscribers,
            after.CanceledBeforeStartCount - before.CanceledBeforeStartCount,
            canceledSubscribers >= tasks.Length / 2
            && after.CanceledBeforeStartCount > before.CanceledBeforeStartCount);
    }

    private static async Task<HighQualityUpgradeReport> MeasureHighQualityUpgradeAsync(
        ThumbnailScheduler motion,
        IReadOnlyList<string> paths,
        AdaptiveFastBrowsePlan plan)
    {
        var selected = paths.Take(Math.Min(8, paths.Count)).ToArray();
        var motionBefore = selected.Count(path =>
            motion.TryGetCached(path, plan.MotionPixels, out _));
        await Task.Delay(plan.IdleHighQualityDelay);
        await using var highQuality = new ThumbnailScheduler(
            maximumConcurrency: 4,
            memoryBudgetBytes: ThumbnailCache.DefaultMemoryBudgetBytes);
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(selected.Select(path => highQuality.LoadAsync(
            path,
            ThumbnailSizingPolicy.GalleryPixels,
            ThumbnailRequestPriority.Visible)));
        clock.Stop();
        var motionAfter = selected.Count(path =>
            motion.TryGetCached(path, plan.MotionPixels, out _));
        var highQualitySnapshot = highQuality.GetSnapshot();
        return new HighQualityUpgradeReport(
            plan.IdleHighQualityDelay.TotalMilliseconds,
            selected.Length,
            motionBefore,
            motionAfter,
            Math.Round(clock.Elapsed.TotalMilliseconds, 3),
            highQualitySnapshot.DecodeCount,
            motionAfter == motionBefore);
    }

    private static MetadataReport MeasureMetadata(int count)
    {
        ForceCollection();
        var before = GC.GetTotalMemory(true);
        var clock = Stopwatch.StartNew();
        var metadata = Enumerable.Range(0, count)
            .Select(index => new SyntheticBrowseMetadata(
                index + 1L,
                $"synthetic/{index:D5}.png",
                256 + index % 7,
                256 + index % 11,
                index % 2 == 0,
                $"tag-{index % 17}"))
            .ToArray();
        clock.Stop();
        var allocated = Math.Max(0, GC.GetTotalMemory(true) - before);
        var ordered = metadata.Select(item => item.Id).SequenceEqual(
            Enumerable.Range(1, count).Select(index => (long)index));
        return new MetadataReport(
            metadata.Length,
            metadata.Select(item => item.Id).Distinct().Count(),
            ordered,
            Math.Round(clock.Elapsed.TotalMilliseconds, 3),
            allocated,
            ContainsPromptOrNotes: false);
    }

    private static string[] CreateFixtures(string root, int count, int targetPixels)
    {
        Directory.CreateDirectory(root);
        var paths = new string[count];
        for (var index = 0; index < count; index++)
        {
            var (widthUnits, heightUnits) = AspectUnits[index % AspectUnits.Length];
            var width = Math.Max(16, targetPixels * widthUnits / Math.Max(widthUnits, heightUnits));
            var height = Math.Max(16, targetPixels * heightUnits / Math.Max(widthUnits, heightUnits));
            var path = Path.Combine(root, $"synthetic-{index:D5}-{width}x{height}.png");
            SyntheticPngWriter.Write(path, width, height, index);
            paths[index] = path;
        }
        return paths;
    }

    private static IReadOnlyList<GateCheck> CreateChecks(
        ScaleReport small,
        ScaleReport medium,
        ScaleReport large,
        VirtualizationReport virtualization)
    {
        var smallInstant = small.Jumps.All(jump =>
            jump.InstantCached == VisibleCount);
        var mediumReady = medium.Jumps.All(jump =>
            jump.Readiness.Last().Ready == VisibleCount);
        var largeReady = large.Jumps.All(jump =>
            jump.Readiness.Last().Ready == VisibleCount);
        return
        [
            new("small-550-unique-fixtures",
                small.UniquePaths == 550 && small.UniqueContentTokens == 550,
                $"{small.UniquePaths}/550 unique paths"),
            new("small-550-instant-after-warm",
                smallInstant,
                $"{small.Jumps.Count(jump => jump.InstantCached == VisibleCount)}/{small.Jumps.Count} jumps at 24/24"),
            new("medium-5000-budgeted-complete-warm",
                medium.Scheduler.DecodeCount >= 5_000 && medium.CacheWithinBudget,
                $"{medium.Scheduler.DecodeCount} decodes; {medium.Scheduler.CachedBytes}/{medium.Scheduler.MemoryBudgetBytes} bytes"),
            new("medium-5000-visible-by-500ms",
                mediumReady,
                $"{medium.Jumps.Count(jump => jump.Readiness.Last().Ready == VisibleCount)}/{medium.Jumps.Count} jumps at 24/24"),
            new("large-30000-light-metadata",
                large.Metadata.Count == 30_000
                && large.Metadata.UniqueIds == 30_000
                && large.Metadata.Ordered
                && !large.Metadata.ContainsPromptOrNotes,
                $"{large.Metadata.Count} ordered lightweight records"),
            new("large-30000-window-bounded",
                large.WarmItemLimit <= 4_096
                && large.Scheduler.CacheEntryCount < 30_000
                && large.CacheWithinBudget,
                $"{large.WarmItemLimit} planned; {large.Scheduler.CacheEntryCount} cached"),
            new("large-30000-visible-by-500ms",
                largeReady,
                $"{large.Jumps.Count(jump => jump.Readiness.Last().Ready == VisibleCount)}/{large.Jumps.Count} jumps at 24/24"),
            new("foreground-visible-preempts-prefetch",
                small.ForegroundPreemption.Passed
                && medium.ForegroundPreemption.Passed
                && large.ForegroundPreemption.Passed,
                $"visible ms {small.ForegroundPreemption.VisibleReadyMs}/{medium.ForegroundPreemption.VisibleReadyMs}/{large.ForegroundPreemption.VisibleReadyMs}"),
            new("generation-cancellation",
                small.Cancellation.Passed
                && medium.Cancellation.Passed
                && large.Cancellation.Passed,
                $"canceled subscribers {small.Cancellation.CanceledSubscribers}/{medium.Cancellation.CanceledSubscribers}/{large.Cancellation.CanceledSubscribers}"),
            new("idle-high-quality-keeps-motion",
                small.HighQualityUpgrade.MotionCachePreserved
                && medium.HighQualityUpgrade.MotionCachePreserved
                && large.HighQualityUpgrade.MotionCachePreserved,
                "motion bitmaps retained through 150ms high-quality upgrade"),
            new("m7.1-virtualization-completeness",
                virtualization.Passed,
                virtualization.Summary)
        ];
    }

    private static VirtualizationReport ReadVirtualizationGate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new VirtualizationReport(
                false,
                0,
                0,
                0,
                0,
                0,
                "virtualization report was not supplied");
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.TryGetProperty("virtualizationProbe", out var milestoneProbe))
        {
            var milestonePassed = milestoneProbe.GetProperty("passed").GetBoolean();
            var milestoneMissing = milestoneProbe.GetProperty("missingVisibleMaximum").GetInt32();
            var milestoneExtra = milestoneProbe.GetProperty("extraOutsideCacheMaximum").GetInt32();
            var milestoneUnrealized = milestoneProbe.GetProperty("unrealizedVisibleMaximum").GetInt32();
            var milestoneP95 = milestoneProbe.GetProperty("frameP95Ms").GetDouble();
            var milestoneP99 = milestoneProbe.GetProperty("frameP99Ms").GetDouble();
            return new VirtualizationReport(
                milestonePassed,
                milestoneMissing,
                milestoneExtra,
                milestoneUnrealized,
                milestoneP95,
                milestoneP99,
                $"M7.1 authority passed={milestonePassed}; maximum missing/extra/unrealized={milestoneMissing}/{milestoneExtra}/{milestoneUnrealized}; P95/P99={milestoneP95}/{milestoneP99}ms");
        }
        var completeness = root.GetProperty("ViewportCompleteness");
        var frames = root.GetProperty("Frames");
        var passed = root.GetProperty("Passed").GetBoolean();
        var missing = completeness.GetProperty("MissingVisibleTotal").GetInt32();
        var extra = completeness.GetProperty("ExtraOutsideCacheTotal").GetInt32();
        var unrealized = completeness.GetProperty("UnrealizedVisibleTotal").GetInt32();
        var p95 = frames.GetProperty("P95Ms").GetDouble();
        var p99 = frames.GetProperty("P99Ms").GetDouble();
        return new VirtualizationReport(
            passed,
            missing,
            extra,
            unrealized,
            p95,
            p99,
            $"passed={passed}; missing/extra/unrealized={missing}/{extra}/{unrealized}; P95/P99={p95}/{p99}ms");
    }

    private static async Task<UiFixtureResult> CreateUiFixtureAsync(string parent)
    {
        var root = Path.Combine(
            Path.GetFullPath(parent),
            $"550-{Guid.NewGuid():N}");
        var paths = new LibraryPaths(root);
        paths.EnsureCreated();
        var imagePaths = CreateFixtures(paths.Originals, 550, ThumbnailSizingPolicy.SmallPixels);
        var repository = new LibraryRepository(paths);
        await repository.InitializeAsync();
        var categories = await repository.GetCategoriesAsync();
        for (var index = 0; index < imagePaths.Length; index++)
        {
            var relative = paths.ToRelative(imagePaths[index]);
            var (widthUnits, heightUnits) = AspectUnits[index % AspectUnits.Length];
            var width = Math.Max(
                16,
                ThumbnailSizingPolicy.SmallPixels * widthUnits / Math.Max(widthUnits, heightUnits));
            var height = Math.Max(
                16,
                ThumbnailSizingPolicy.SmallPixels * heightUnits / Math.Max(widthUnits, heightUnits));
            var category = categories[index % categories.Count];
            await repository.SaveAsync(new SaveItemInput(
                new AssetInput(
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"m7.2-ui-{index}"))),
                    relative,
                    relative,
                    relative,
                    width,
                    height,
                    "PNG"),
                $"M7.2 合成提示词 {index:D4}，用于隔离极速浏览验收。",
                $"合成备注 {index:D4}",
                category.Id,
                [$"合成-{index % 13}", index % 2 == 0 ? "竖图" : "横图"]));
        }

        var settingsPath = Path.Combine(Path.GetDirectoryName(root)!, "m7.2-ui-550-settings.json");
        var settings = new
        {
            LibraryRoot = root,
            OldestFirst = false,
            CaptureListeningEnabled = false,
            CaptureQuickEditEnabled = false,
            ReducedMotionEnabled = true,
            EdgeMenusAlwaysVisible = true,
            OnlineAiEnabled = false,
            ExternalFolders = Array.Empty<object>(),
            GalleryLayouts = new Dictionary<string, object>()
        };
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        return new UiFixtureResult(
            550,
            imagePaths.Length,
            false,
            false,
            Path.GetRelativePath(Environment.CurrentDirectory, settingsPath)
                .Replace('\\', '/'),
            settingsPath);
    }

    private static bool ContainsAbsoluteWindowsPath(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            value,
            "[A-Za-z]:\\\\\\\\",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Temporary probe fixtures could not be removed.");
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Temporary probe fixtures could not be removed.");
        }
    }

    private sealed record ProbeOptions(
        string OutputPath,
        string? VirtualizationPath,
        string? UiLibraryParent)
    {
        public static ProbeOptions Parse(string[] args)
        {
            string? output = null;
            string? virtualization = null;
            string? uiLibraryParent = null;
            for (var index = 0; index < args.Length; index++)
            {
                var value = args[index];
                if (string.Equals(value, "--output", StringComparison.OrdinalIgnoreCase))
                {
                    output = RequiredValue(args, ref index, value);
                }
                else if (string.Equals(value, "--virtualization", StringComparison.OrdinalIgnoreCase))
                {
                    virtualization = RequiredValue(args, ref index, value);
                }
                else if (string.Equals(value, "--ui-library-parent", StringComparison.OrdinalIgnoreCase))
                {
                    uiLibraryParent = RequiredValue(args, ref index, value);
                }
                else if (!value.StartsWith("--", StringComparison.Ordinal) && output is null)
                {
                    output = value;
                }
                else
                {
                    throw new ArgumentException($"Unknown argument: {value}");
                }
            }
            return new ProbeOptions(
                Path.GetFullPath(output ?? Path.Combine(
                    "docs",
                    "performance",
                    "2026-07-30-m7.2-adaptive-fast-browsing.json")),
                virtualization is null ? null : Path.GetFullPath(virtualization),
                uiLibraryParent is null ? null : Path.GetFullPath(uiLibraryParent));
        }

        private static string RequiredValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{option} requires a value.");
            }
            return args[index];
        }
    }

    private sealed record SyntheticBrowseMetadata(
        long Id,
        string RelativeThumbnailPath,
        int Width,
        int Height,
        bool IsFavorite,
        string Tags);

    private sealed record MetadataReport(
        int Count,
        int UniqueIds,
        bool Ordered,
        double BuildMs,
        long RetainedManagedBytes,
        bool ContainsPromptOrNotes);

    private sealed record ReadinessSample(int AtMs, int Ready, int Visible);

    private sealed record JumpReport(
        double Percentage,
        int FirstIndex,
        int Visible,
        int InstantCached,
        double AllReadyMs,
        IReadOnlyList<ReadinessSample> Readiness);

    private sealed record ForegroundPreemptionReport(
        double VisibleReadyMs,
        int MaximumObservedConcurrency,
        int RunningPrefetchAtEnd,
        bool Passed);

    private sealed record CancellationReport(
        int Requested,
        int CanceledSubscribers,
        long CanceledBeforeStart,
        bool Passed);

    private sealed record HighQualityUpgradeReport(
        double IdleDelayMs,
        int Requested,
        int MotionCachedBefore,
        int MotionCachedAfter,
        double HighQualityReadyMs,
        long HighQualityDecodes,
        bool MotionCachePreserved);

    private sealed record Percentiles(double P50Ms, double P95Ms, double P99Ms, double MaximumMs)
    {
        public static Percentiles From(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            if (sorted.Length == 0) return new Percentiles(0, 0, 0, 0);
            return new Percentiles(
                Value(sorted, 0.50),
                Value(sorted, 0.95),
                Value(sorted, 0.99),
                Math.Round(sorted[^1], 3));
        }

        private static double Value(double[] sorted, double percentile)
        {
            var index = Math.Clamp(
                (int)Math.Ceiling(percentile * sorted.Length) - 1,
                0,
                sorted.Length - 1);
            return Math.Round(sorted[index], 3);
        }
    }

    private sealed record ScaleReport(
        int ItemCount,
        string Tier,
        int MotionPixels,
        int WarmItemLimit,
        int BatchSize,
        int MotionDecodeConcurrency,
        long MotionMemoryBudgetBytes,
        double FixtureCreationMs,
        int UniquePaths,
        int UniqueContentTokens,
        MetadataReport Metadata,
        int ExpectedWarmRequests,
        Percentiles WarmRequestLatency,
        IReadOnlyList<JumpReport> Jumps,
        ForegroundPreemptionReport ForegroundPreemption,
        CancellationReport Cancellation,
        HighQualityUpgradeReport HighQualityUpgrade,
        ThumbnailSchedulerSnapshot Scheduler,
        bool CacheWithinBudget);

    private sealed record VirtualizationReport(
        bool Passed,
        int MissingVisibleTotal,
        int ExtraOutsideCacheTotal,
        int UnrealizedVisibleTotal,
        double FrameP95Ms,
        double FrameP99Ms,
        string Summary);

    private sealed record GateCheck(string Name, bool Passed, string Evidence);

    private sealed record UiFixtureResult(
        int ItemCount,
        int UniqueImageCount,
        bool CaptureListeningEnabled,
        bool OnlineAiEnabled,
        string RelativeSettingsHint,
        string SettingsPath);

    private static class SyntheticPngWriter
    {
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static void Write(
            string path,
            int width,
            int height,
            int contentToken)
        {
            var rowBytes = checked(width * 3 + 1);
            var raw = new byte[checked(rowBytes * height)];
            var red = (byte)(37 + contentToken * 17);
            var green = (byte)(71 + contentToken * 29);
            var blue = (byte)(113 + contentToken * 43);
            for (var y = 0; y < height; y++)
            {
                var rowStart = y * rowBytes;
                raw[rowStart] = 0;
                for (var x = 0; x < width; x++)
                {
                    var pixel = rowStart + 1 + x * 3;
                    var stripe = (byte)(((x / 24) + (y / 24)) % 2 == 0 ? 22 : 0);
                    raw[pixel] = (byte)(red + stripe);
                    raw[pixel + 1] = (byte)(green + stripe);
                    raw[pixel + 2] = (byte)(blue + stripe);
                }
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(Signature);
            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
            BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
            header[8] = 8;
            header[9] = 2;
            header[10] = 0;
            header[11] = 0;
            header[12] = 0;
            WriteChunk(stream, "IHDR"u8, header);
            WriteChunk(
                stream,
                "tEXt"u8,
                Encoding.ASCII.GetBytes($"pv-index\0{contentToken:D8}"));
            WriteChunk(stream, "IDAT"u8, compressed.ToArray());
            WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
        }

        private static void WriteChunk(
            Stream stream,
            ReadOnlySpan<byte> type,
            ReadOnlySpan<byte> data)
        {
            Span<byte> value = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(value, data.Length);
            stream.Write(value);
            stream.Write(type);
            stream.Write(data);
            var crc = ComputeCrc(type, data);
            BinaryPrimitives.WriteUInt32BigEndian(value, crc);
            stream.Write(value);
        }

        private static uint ComputeCrc(
            ReadOnlySpan<byte> type,
            ReadOnlySpan<byte> data)
        {
            var crc = uint.MaxValue;
            foreach (var value in type) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            return crc ^ uint.MaxValue;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) == 1
                        ? 0xedb88320U ^ (value >> 1)
                        : value >> 1;
                }
                table[index] = value;
            }
            return table;
        }
    }
}
