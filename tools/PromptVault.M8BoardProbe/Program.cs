using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.Core;

if (args.Length != 3)
{
    Console.Error.WriteLine(
        "Usage: PromptVault.M8BoardProbe <new-fixture-parent> <ui-settings.json> <report.json>");
    return 2;
}

var fixtureParent = Path.GetFullPath(args[0]);
var settingsPath = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args[2]);
var sizes = new[] { 20, 500, 5_000 };

Directory.CreateDirectory(fixtureParent);
var results = new List<FixtureResult>(sizes.Length);
foreach (var size in sizes)
{
    var fixtureRoot = Path.Combine(fixtureParent, $"board-{size}");
    if (Directory.Exists(fixtureRoot) && Directory.EnumerateFileSystemEntries(fixtureRoot).Any())
    {
        Console.Error.WriteLine($"Refusing to reuse non-empty fixture directory: board-{size}");
        return 3;
    }

    results.Add(await CreateFixtureAsync(fixtureRoot, size));
}

var uiFixture = results.Single(result => result.BoardItemCount == 20);
var uiRoot = Path.Combine(fixtureParent, uiFixture.RelativeRoot);
Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
await File.WriteAllTextAsync(
    settingsPath,
    JsonSerializer.Serialize(
        new
        {
            LibraryRoot = uiRoot,
            OldestFirst = false,
            CaptureListeningEnabled = false,
            CaptureQuickEditEnabled = false,
            ReducedMotionEnabled = true,
            InspectorPinned = false,
            EdgeMenusAlwaysVisible = true,
            OnlineAiEnabled = false,
            OnlineAiEndpoint = "",
            OnlineAiModel = ""
        },
        ProbeJson.Options));

var passed = results.All(result =>
    result.Passed
    && result.ViewportQueryP95Ms <= 0.10
    && result.MaximumRealizedCandidateCount < 300);
var report = new
{
    Milestone = "M8-00-pureref-board-baseline",
    GeneratedAt = DateTimeOffset.Now,
    DataKind = "synthetic",
    Environment = new
    {
        Runtime = RuntimeInformation.FrameworkDescription,
        OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString()
    },
    FixtureContract = new
    {
        Sizes = sizes,
        AspectRatios = new[] { "1:4", "1:2", "2:3", "1:1", "3:2", "2:1", "4:1" },
        HasRotation = true,
        HasCrop = true,
        HasTwoGroups = true,
        HasTwoNotes = true,
        IntentionallyMissingUniqueSourceCount = 1,
        UsesFixedSeed = true
    },
    Fixtures = results,
    UiFixture = new
    {
        uiFixture.RelativeRoot,
        SettingsFile = Path.GetFileName(settingsPath),
        uiFixture.BoardName,
        uiFixture.BoardItemCount,
        CaptureListeningEnabled = false,
        CaptureQuickEditEnabled = false,
        OnlineAiEnabled = false
    },
    Gates = new
    {
        ViewportQueryP95LimitMs = 0.10,
        MaximumRealizedCandidateLimit = 300,
        Passed = passed
    },
    DataSafety = new
    {
        SyntheticFixturesOnly = true,
        RealLibraryOpened = false,
        RealLibraryModified = false,
        RealLibraryDeleted = false,
        ClipboardUsed = false,
        NetworkUsed = false,
        ApiKeyUsed = false,
        AbsolutePathsIncludedInReport = false
    },
    Passed = passed
};

var json = JsonSerializer.Serialize(report, ProbeJson.Options);
Console.WriteLine(json);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
await File.WriteAllTextAsync(reportPath, json);
return passed ? 0 : 1;

static async Task<FixtureResult> CreateFixtureAsync(string root, int itemCount)
{
    var seed = Stopwatch.StartNew();
    var repository = new LibraryRepository(new LibraryPaths(root));
    await repository.InitializeAsync();
    var sources = new List<SyntheticSource>();
    var definitions = new[]
    {
        new SourceDefinition("missing-square", 720, 720, (235, 78, 144), true),
        new SourceDefinition("ratio-1x4", 320, 1280, (34, 205, 224), false),
        new SourceDefinition("ratio-1x2", 480, 960, (246, 190, 63), false),
        new SourceDefinition("ratio-2x3", 640, 960, (119, 93, 232), false),
        new SourceDefinition("ratio-1x1", 720, 720, (72, 196, 126), false),
        new SourceDefinition("ratio-3x2", 960, 640, (240, 116, 68), false),
        new SourceDefinition("ratio-2x1", 1000, 500, (63, 134, 235), false),
        new SourceDefinition("ratio-4x1", 1280, 320, (212, 94, 235), false)
    };

    for (var index = 0; index < definitions.Length; index++)
    {
        var definition = definitions[index];
        var hash = $"m8-{definition.Name}";
        var original = Path.Combine(repository.Paths.Originals, $"{hash}.png");
        var small = Path.Combine(repository.Paths.SmallThumbnails, $"{hash}.png");
        var medium = Path.Combine(repository.Paths.MediumThumbnails, $"{hash}.png");
        SaveImage(original, definition.Width, definition.Height, definition.Color, index);
        File.Copy(original, small, true);
        File.Copy(original, medium, true);
        var saved = await repository.SaveAsync(new SaveItemInput(
            new AssetInput(
                hash,
                repository.Paths.ToRelative(original),
                repository.Paths.ToRelative(small),
                repository.Paths.ToRelative(medium),
                definition.Width,
                definition.Height,
                "png"),
            $"M8 synthetic {definition.Name}",
            "Synthetic data for isolated M8 board validation",
            null,
            ["M8", "synthetic", definition.Name]));
        sources.Add(new SyntheticSource(
            saved.ItemId,
            definition.Width,
            definition.Height,
            repository.Paths.ToRelative(original),
            definition.IntentionallyMissing));
    }

    var board = await repository.CreateBoardAsync($"M8 基线画板 {itemCount:N0}");
    var primaryGroup = await repository.CreateBoardGroupAsync(board.Id, "主参考组");
    var secondaryGroup = await repository.CreateBoardGroupAsync(board.Id, "辅助参考组");
    var placements = new BoardItemPlacementInput[itemCount];
    var rotations = new[] { 0d, -7d, 5d, 15d, 45d, 90d };
    for (var index = 0; index < itemCount; index++)
    {
        var source = index == 0
            ? sources[0]
            : sources[1 + (index - 1) % (sources.Count - 1)];
        var width = 210d + index % 3 * 15d;
        var height = width * source.Height / source.Width;
        var column = index % 100;
        var row = index / 100;
        var cropped = index > 0 && index % 11 == 0;
        long? groupId = index < Math.Min(6, itemCount)
            ? primaryGroup.Id
            : index < Math.Min(12, itemCount)
                ? secondaryGroup.Id
                : null;
        placements[index] = new BoardItemPlacementInput(
            source.CollectionItemId,
            column * 270d + index % 4 * 5d,
            row * 960d + index % 5 * 7d,
            width,
            height,
            index,
            rotations[index % rotations.Length],
            cropped ? 0.08 : 0,
            cropped ? 0.04 : 0,
            cropped ? 0.05 : 0,
            cropped ? 0.03 : 0,
            groupId);
    }

    await repository.AddBoardItemsAsync(board.Id, placements);
    await repository.UpdateBoardBackgroundAsync(board.Id, "blueprint");
    await repository.AddBoardNoteAsync(
        board.Id,
        "M8 合成验收便签\n固定种子 · 不连接真实图库",
        420,
        280,
        360,
        240,
        itemCount + 1,
        "yellow");
    await repository.AddBoardNoteAsync(
        board.Id,
        "旋转、裁剪、分组、缺失源均为合成数据。",
        1_240,
        1_400,
        420,
        260,
        itemCount + 2,
        "blue");
    await repository.UpdateBoardViewAsync(board.Id, 90, 70, 1.15);

    var missing = sources.Single(source => source.IntentionallyMissing);
    File.Delete(repository.Paths.ToAbsolute(missing.OriginalPath));
    seed.Stop();

    var restartLoad = Stopwatch.StartNew();
    var restarted = new LibraryRepository(new LibraryPaths(root));
    await restarted.InitializeAsync();
    var document = await restarted.GetBoardDocumentAsync(board.Id)
        ?? throw new InvalidOperationException("Synthetic M8 board was not persisted.");
    restartLoad.Stop();

    var viewport = new BoardViewport(90, 70, 1.15, 2_560, 1_707);
    var querySamples = new double[301];
    var visibleMax = 0;
    for (var iteration = 0; iteration < querySamples.Length; iteration++)
    {
        var moving = viewport with
        {
            OffsetX = 90 - iteration * 18,
            OffsetY = 70 - iteration * 6
        };
        var query = Stopwatch.StartNew();
        visibleMax = Math.Max(
            visibleMax,
            BoardViewportEngine.QueryVisible(document.Items, moving).Count);
        query.Stop();
        querySamples[iteration] = query.Elapsed.TotalMilliseconds;
    }
    Array.Sort(querySamples);

    var ratios = document.Items
        .Where(item => item.NaturalWidth > 0 && item.NaturalHeight > 0)
        .Select(item => RatioLabel(item.NaturalWidth, item.NaturalHeight))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    var expectedRatios = new HashSet<string>(
        ["1:4", "1:2", "2:3", "1:1", "3:2", "2:1", "4:1"],
        StringComparer.Ordinal);
    var missingUniqueSources = document.Items
        .Select(item => item.SourcePathSnapshot)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count(relative => !File.Exists(restarted.Paths.ToAbsolute(relative)));
    var rotatedCount = document.Items.Count(item => Math.Abs(item.Rotation) > 0.001);
    var croppedCount = document.Items.Count(item =>
        item.CropLeft > 0 || item.CropTop > 0 || item.CropRight > 0 || item.CropBottom > 0);
    var p50 = Percentile(querySamples, 0.50);
    var p95 = Percentile(querySamples, 0.95);
    var p99 = Percentile(querySamples, 0.99);
    var passed = document.Items.Count == itemCount
        && document.Groups.Count == 2
        && document.Notes.Count == 2
        && document.Board.BackgroundStyle == "blueprint"
        && expectedRatios.SetEquals(ratios)
        && rotatedCount > 0
        && croppedCount > 0
        && missingUniqueSources == 1
        && p95 <= 0.10
        && visibleMax < 300;

    return new FixtureResult(
        RelativeRoot: $"board-{itemCount}",
        BoardName: document.Board.Name,
        LibraryItemCount: sources.Count,
        BoardItemCount: document.Items.Count,
        GroupCount: document.Groups.Count,
        NoteCount: document.Notes.Count,
        BackgroundStyle: document.Board.BackgroundStyle,
        AspectRatios: ratios,
        RotatedItemCount: rotatedCount,
        CroppedItemCount: croppedCount,
        IntentionallyMissingUniqueSourceCount: missingUniqueSources,
        SeedMs: Math.Round(seed.Elapsed.TotalMilliseconds, 3),
        RestartDocumentLoadMs: Math.Round(restartLoad.Elapsed.TotalMilliseconds, 3),
        ViewportQueries: querySamples.Length,
        ViewportQueryP50Ms: Math.Round(p50, 4),
        ViewportQueryP95Ms: Math.Round(p95, 4),
        ViewportQueryP99Ms: Math.Round(p99, 4),
        ViewportQueryMaxMs: Math.Round(querySamples[^1], 4),
        MaximumRealizedCandidateCount: visibleMax,
        Passed: passed);
}

static double Percentile(IReadOnlyList<double> sorted, double percentile) =>
    sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];

static string RatioLabel(int width, int height)
{
    var divisor = GreatestCommonDivisor(width, height);
    return $"{width / divisor}:{height / divisor}";
}

static int GreatestCommonDivisor(int left, int right)
{
    while (right != 0)
    {
        (left, right) = (right, left % right);
    }
    return Math.Abs(left);
}

static void SaveImage(
    string path,
    int width,
    int height,
    (int R, int G, int B) color,
    int variant)
{
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var offset = (y * width + x) * 4;
        var checker = ((x / 64 + y / 64 + variant) % 2 == 0) ? 24 : -16;
        var diagonal = (x + y + variant * 19) % 127 < 10 ? 28 : 0;
        pixels[offset] = (byte)Math.Clamp(color.B + checker + diagonal, 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(color.G + checker, 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(color.R + checker - diagonal, 0, 255);
        pixels[offset + 3] = 255;
    }

    var bitmap = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        pixels,
        width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

internal sealed record SourceDefinition(
    string Name,
    int Width,
    int Height,
    (int R, int G, int B) Color,
    bool IntentionallyMissing);

internal sealed record SyntheticSource(
    long CollectionItemId,
    int Width,
    int Height,
    string OriginalPath,
    bool IntentionallyMissing);

internal sealed record FixtureResult(
    string RelativeRoot,
    string BoardName,
    int LibraryItemCount,
    int BoardItemCount,
    int GroupCount,
    int NoteCount,
    string BackgroundStyle,
    IReadOnlyList<string> AspectRatios,
    int RotatedItemCount,
    int CroppedItemCount,
    int IntentionallyMissingUniqueSourceCount,
    double SeedMs,
    double RestartDocumentLoadMs,
    int ViewportQueries,
    double ViewportQueryP50Ms,
    double ViewportQueryP95Ms,
    double ViewportQueryP99Ms,
    double ViewportQueryMaxMs,
    int MaximumRealizedCandidateCount,
    bool Passed);

internal static class ProbeJson
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}
