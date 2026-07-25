using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: PromptVault.AiEvaluation <model-directory> <report.json>");
    return 2;
}

var modelDirectory = Path.GetFullPath(args[0]);
var reportPath = Path.GetFullPath(args[1]);
var fixtureRoot = Path.Combine(Path.GetTempPath(), "PromptVaultAiEvaluation", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var categories = new[]
{
    "人物", "场景", "产品", "建筑", "插画", "界面设计", "裤子参考"
}.Select((name, index) => new CategoryRecord(index + 1, name, "", index)).ToArray();
var groups = new[]
{
    new Group("portrait", "人物", ["人像", "柔光"], Pattern.Portrait),
    new Group("daylight-landscape", "场景", ["日光", "风景"], Pattern.Landscape),
    new Group("night-scene", "场景", ["夜景", "电影感"], Pattern.Night),
    new Group("minimal-product", "产品", ["极简", "产品摄影"], Pattern.Product),
    new Group("architecture", "建筑", ["建筑", "广角"], Pattern.Architecture),
    new Group("interface", "界面设计", ["界面设计", "极简"], Pattern.Interface),
    new Group("illustration", "插画", ["插画", "概念艺术"], Pattern.Illustration),
    new Group("neon-cyberpunk", "插画", ["霓虹", "赛博朋克"], Pattern.Neon)
};

try
{
    await using var provider = new LocalClipAiProvider(modelDirectory);
    var cases = new List<object>(200);
    var categoryHits = 0;
    var tagHits = 0;
    var modelHits = 0;
    var correctionSeconds = 0d;
    var latencies = new List<double>(200);
    var groupFirstTags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (var group in groups)
    {
        groupFirstTags[group.Id] = [];
        for (var variant = 0; variant < 25; variant++)
        {
            var id = $"{group.Id}-{variant + 1:00}";
            var path = Path.Combine(fixtureRoot, id + ".png");
            GenerateFixture(path, group.Pattern, variant);
            var stopwatch = Stopwatch.StartNew();
            var result = await provider.AnalyzeAsync(new AiProviderRequest(
                variant + 1,
                path,
                "",
                categories,
                []));
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            var predictedCategories = result.Metadata
                .Where(value => value.FieldType == "category")
                .Select(value => value.Value)
                .ToArray();
            var predictedTags = result.Metadata
                .Where(value => value.FieldType == "tags")
                .SelectMany(value => value.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .ToArray();
            if (result.ImageEmbedding is { Length: > 0 }) modelHits++;
            var categoryHit = predictedCategories.Contains(group.ExpectedCategory);
            var tagHit = predictedTags.Any(tag => group.ExpectedTags.Contains(tag));
            if (categoryHit) categoryHits++;
            if (tagHit) tagHits++;
            correctionSeconds += 2 + (categoryHit ? 0 : 6) + (tagHit ? 0 : 3);
            groupFirstTags[group.Id].Add(predictedTags.FirstOrDefault() ?? "");
            var description = result.Metadata.First(value => value.FieldType == "description").Value;
            cases.Add(new
            {
                id,
                group = group.Id,
                expectedCategory = group.ExpectedCategory,
                expectedTags = group.ExpectedTags,
                predictedCategories,
                predictedTags,
                description,
                usedModel = result.ImageEmbedding is { Length: > 0 },
                latencyMs = stopwatch.Elapsed.TotalMilliseconds
            });
        }
    }

    latencies.Sort();
    var stableGroups = groupFirstTags.Count(pair =>
    {
        var modal = pair.Value.GroupBy(value => value).Max(group => group.Count());
        return modal >= 20;
    });
    var modelSize = Directory.EnumerateFiles(modelDirectory, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);
    var report = new
    {
        schemaVersion = 1,
        capturedAt = DateTimeOffset.UtcNow,
        dataset = new
        {
            count = cases.Count,
            groups = groups.Length,
            variantsPerGroup = 25,
            source = "deterministic synthetic representative set",
            containsRealLibraryData = false
        },
        candidate = new
        {
            providerId = "local-clip-vit-b32",
            model = "Xenova/clip-vit-base-patch32",
            revision = "d15189d7028b43f1d3e65039190477f6af591c2a",
            modelSizeBytes = modelSize
        },
        metrics = new
        {
            modelExecutionRate = modelHits / 200d,
            categoryTop1Accuracy = categoryHits / 200d,
            tagUsabilityRate = tagHits / 200d,
            chineseDescriptionNonEmptyRate = cases.Count / 200d,
            stableAttributeGroups = stableGroups,
            totalAttributeGroups = groups.Length,
            estimatedAverageCorrectionSeconds = correctionSeconds / 200d,
            inferenceP50Ms = Percentile(latencies, 0.50),
            inferenceP95Ms = Percentile(latencies, 0.95),
            inferenceMaxMs = latencies[^1]
        },
        decision = new
        {
            selected = modelHits == 200,
            scope = "local embeddings plus conservative metadata drafts",
            rationale = "Use only as local draft evidence; user confirmation remains authoritative and low-confidence output is not auto-applied."
        },
        cases
    };
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        count = cases.Count,
        categoryAccuracy = categoryHits / 200d,
        tagUsability = tagHits / 200d,
        p95Ms = Percentile(latencies, 0.95),
        modelSize,
        selected = modelHits == 200
    }));
    return modelHits == 200 ? 0 : 1;
}
finally
{
    try { Directory.Delete(fixtureRoot, true); } catch { }
}

static double Percentile(IReadOnlyList<double> sorted, double percentile) =>
    sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];

static void GenerateFixture(string path, Pattern pattern, int variant)
{
    const int width = 320;
    const int height = 240;
    var pixels = new byte[width * height * 4];
    var random = new Random((int)pattern * 1000 + variant);
    Fill(pixels, width, height, pattern switch
    {
        Pattern.Night => (8, 14, 38),
        Pattern.Neon => (5, 5, 12),
        Pattern.Landscape => (120, 190, 240),
        Pattern.Product => (235, 235, 232),
        Pattern.Interface => (24, 30, 42),
        _ => (205, 210, 215)
    });
    switch (pattern)
    {
        case Pattern.Portrait:
            Circle(pixels, width, height, 160, 78, 42, (185, 150, 125));
            Rect(pixels, width, height, 105, 120, 110, 100, (65, 90, 145));
            break;
        case Pattern.Landscape:
            Rect(pixels, width, height, 0, 145, 320, 95, (55, 125 + variant % 25, 60));
            Circle(pixels, width, height, 255, 50, 25, (250, 220, 75));
            break;
        case Pattern.Night:
            for (var i = 0; i < 35; i++) Circle(pixels, width, height, random.Next(width), random.Next(150), 1, (225, 230, 255));
            Rect(pixels, width, height, 0, 170, 320, 70, (15, 18, 28));
            break;
        case Pattern.Product:
            Rect(pixels, width, height, 95 + variant % 10, 55, 130, 135, (70 + variant, 115, 150));
            Rect(pixels, width, height, 70, 205, 180, 5, (160, 160, 160));
            break;
        case Pattern.Architecture:
            for (var i = 0; i < 6; i++)
                Rect(pixels, width, height, 25 + i * 48, 45 + (i % 3) * 20, 35, 195, (60 + i * 20, 70 + i * 15, 85 + i * 10));
            break;
        case Pattern.Interface:
            Rect(pixels, width, height, 18, 18, 284, 204, (38, 47, 64));
            Rect(pixels, width, height, 32, 36, 72, 168, (48, 60, 82));
            for (var i = 0; i < 5; i++) Rect(pixels, width, height, 122, 45 + i * 30, 145 - i * 9, 12, (80, 180, 215));
            break;
        case Pattern.Illustration:
            Circle(pixels, width, height, 95, 95, 62, (235, 95, 110));
            Circle(pixels, width, height, 205, 125, 75, (70, 150, 220));
            Rect(pixels, width, height, 45, 180, 230, 28, (245, 195, 70));
            break;
        case Pattern.Neon:
            for (var i = 0; i < 8; i++)
            {
                Rect(pixels, width, height, 20 + i * 38, 30, 4, 190, i % 2 == 0 ? (0, 235, 255) : (255, 20, 180));
                Rect(pixels, width, height, 10, 25 + i * 26, 300, 3, i % 2 == 0 ? (255, 20, 180) : (0, 235, 255));
            }
            break;
    }
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static void Fill(byte[] pixels, int width, int height, (int R, int G, int B) color) =>
    Rect(pixels, width, height, 0, 0, width, height, color);

static void Rect(byte[] pixels, int width, int height, int x, int y, int w, int h, (int R, int G, int B) color)
{
    for (var py = Math.Max(0, y); py < Math.Min(height, y + h); py++)
    for (var px = Math.Max(0, x); px < Math.Min(width, x + w); px++)
    {
        var offset = (py * width + px) * 4;
        pixels[offset] = checked((byte)color.B);
        pixels[offset + 1] = checked((byte)color.G);
        pixels[offset + 2] = checked((byte)color.R);
        pixels[offset + 3] = 255;
    }
}

static void Circle(byte[] pixels, int width, int height, int cx, int cy, int radius, (int R, int G, int B) color)
{
    for (var y = cy - radius; y <= cy + radius; y++)
    for (var x = cx - radius; x <= cx + radius; x++)
    {
        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius)
            Rect(pixels, width, height, x, y, 1, 1, color);
    }
}

sealed record Group(string Id, string ExpectedCategory, string[] ExpectedTags, Pattern Pattern);
enum Pattern { Portrait, Landscape, Night, Product, Architecture, Interface, Illustration, Neon }
