using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.Core;

if (args.Length is < 2 or > 4)
{
    Console.Error.WriteLine(
        "Usage: PromptVault.M5BoardProbe <new-library-root> <settings.json> [report.json] [item-count]");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var settingsPath = Path.GetFullPath(args[1]);
var reportPath = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
var itemCount = args.Length >= 4 ? int.Parse(args[3]) : 5_000;
if (File.Exists(Path.Combine(root, "library.db")))
{
    Console.Error.WriteLine("Refusing to seed a directory that already contains library.db.");
    return 3;
}

var repository = new LibraryRepository(new LibraryPaths(root));
await repository.InitializeAsync();
var savedItems = new List<(long Id, int Width, int Height)>();
var palette = new[]
{
    (34, 205, 224), (235, 78, 144), (246, 190, 63), (119, 93, 232),
    (72, 196, 126), (240, 116, 68), (63, 134, 235), (212, 94, 235),
    (154, 210, 70), (226, 71, 71), (73, 216, 180), (178, 139, 96)
};
for (var index = 0; index < palette.Length; index++)
{
    var hash = $"m5-synthetic-{index + 1:D2}";
    var width = index % 3 == 0 ? 960 : index % 3 == 1 ? 720 : 640;
    var height = index % 3 == 0 ? 640 : index % 3 == 1 ? 960 : 640;
    var original = Path.Combine(repository.Paths.Originals, $"{hash}.png");
    var small = Path.Combine(repository.Paths.SmallThumbnails, $"{hash}.png");
    var medium = Path.Combine(repository.Paths.MediumThumbnails, $"{hash}.png");
    SaveImage(original, width, height, palette[index], index);
    File.Copy(original, small, true);
    File.Copy(original, medium, true);
    var saved = await repository.SaveAsync(new SaveItemInput(
        new AssetInput(
            hash,
            repository.Paths.ToRelative(original),
            repository.Paths.ToRelative(small),
            repository.Paths.ToRelative(medium),
            width,
            height,
            "png"),
        $"M5 synthetic reference {index + 1}",
        "Synthetic data for isolated 4K board validation",
        null,
        ["M5", "synthetic", index % 2 == 0 ? "cyan" : "magenta"]));
    savedItems.Add((saved.ItemId, width, height));
}

var board = await repository.CreateBoardAsync("M5 4K 验收画板");
var placements = new BoardItemPlacementInput[itemCount];
for (var index = 0; index < placements.Length; index++)
{
    var source = savedItems[index % savedItems.Count];
    var column = index % 100;
    var row = index / 100;
    var width = 240d;
    var height = width * source.Height / source.Width;
    placements[index] = new BoardItemPlacementInput(
        source.Id,
        column * 270,
        row * 230,
        width,
        height,
        index,
        index % 19 == 0 ? -3 : index % 23 == 0 ? 3 : 0);
}
var insert = Stopwatch.StartNew();
var inserted = await repository.AddBoardItemsAsync(board.Id, placements);
insert.Stop();
var group = await repository.CreateBoardGroupAsync(board.Id, "合成参考组");
await repository.SetBoardItemsGroupAsync(board.Id, inserted.Take(12).Select(item => item.Id).ToArray(), group.Id);
await repository.UpdateBoardBackgroundAsync(board.Id, "blueprint");
await repository.AddBoardNoteAsync(
    board.Id,
    "M5 合成验收便签\n拖动标题栏，直接编辑正文。",
    420,
    280,
    360,
    240,
    1,
    "yellow");
await repository.UpdateBoardViewAsync(board.Id, 90, 70, 1.15);
var intentionallyMissingSource = Path.Combine(
    repository.Paths.Originals,
    "m5-synthetic-01.png");
File.Delete(intentionallyMissingSource);

var load = Stopwatch.StartNew();
var document = await repository.GetBoardDocumentAsync(board.Id);
load.Stop();
if (document is null) throw new InvalidOperationException("Synthetic board was not persisted.");
var viewport = new BoardViewport(90, 70, 1.15, 2560, 1707);
var querySamples = new double[301];
var visibleMax = 0;
for (var iteration = 0; iteration < querySamples.Length; iteration++)
{
    var moving = viewport with { OffsetX = 90 - iteration * 18, OffsetY = 70 - iteration * 6 };
    var query = Stopwatch.StartNew();
    visibleMax = Math.Max(
        visibleMax,
        BoardViewportEngine.QueryVisible(document.Items, moving).Count);
    query.Stop();
    querySamples[iteration] = query.Elapsed.TotalMilliseconds;
}
Array.Sort(querySamples);

Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
{
    LibraryRoot = root,
    CaptureListeningEnabled = false,
    ReducedMotionEnabled = true,
    EdgeMenusAlwaysVisible = true
}, new JsonSerializerOptions { WriteIndented = true }));

var result = new
{
    generatedAt = DateTimeOffset.Now,
    dataKind = "synthetic",
    libraryItemCount = savedItems.Count,
    boardItemCount = document.Items.Count,
    groupCount = document.Groups.Count,
    noteCount = document.Notes.Count,
    backgroundStyle = document.Board.BackgroundStyle,
    intentionallyMissingSource = !File.Exists(intentionallyMissingSource),
    insertMs = Math.Round(insert.Elapsed.TotalMilliseconds, 3),
    restartDocumentLoadMs = Math.Round(load.Elapsed.TotalMilliseconds, 3),
    viewportQueries = querySamples.Length,
    viewportQueryP50Ms = Math.Round(querySamples[querySamples.Length / 2], 4),
    viewportQueryP95Ms = Math.Round(querySamples[(int)Math.Ceiling(querySamples.Length * 0.95) - 1], 4),
    viewportQueryMaxMs = Math.Round(querySamples[^1], 4),
    maximumRealizedCandidateCount = visibleMax,
    captureListening = false,
    passed = document.Items.Count == itemCount
        && document.Groups.Count == 1
        && document.Notes.Count == 1
        && document.Board.BackgroundStyle == "blueprint"
        && !File.Exists(intentionallyMissingSource)
        && querySamples[(int)Math.Ceiling(querySamples.Length * 0.95) - 1] <= 5
        && visibleMax < 300
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (reportPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath, json);
}
return result.passed ? 0 : 1;

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
        var checker = ((x / 80 + y / 80 + variant) % 2 == 0) ? 22 : -14;
        var vignette = (int)(24 * Math.Abs(x - width / 2d) / width);
        pixels[offset] = (byte)Math.Clamp(color.B + checker - vignette, 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(color.G + checker - vignette, 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(color.R + checker - vignette, 0, 255);
        pixels[offset + 3] = 255;
    }
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}
