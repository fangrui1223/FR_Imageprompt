using System.Text.Json;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.Core;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PromptVault.M4UiSeed <library-root> <settings.json>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var settingsPath = Path.GetFullPath(args[1]);
var paths = new LibraryPaths(root);
var repository = new LibraryRepository(paths);
await repository.InitializeAsync();
var colors = new[] { (30, 190, 220), (220, 70, 150), (245, 185, 55) };
var prompts = new[] { "cyan minimal product", "magenta neon portrait", "golden daylight architecture" };
var vectors = new[]
{
    new[] { 1f, 0f, 0f, 0f },
    new[] { 0.96f, 0.14f, 0f, 0f },
    new[] { 0.12f, 0.1f, 0.98f, 0f }
};
for (var index = 0; index < colors.Length; index++)
{
    var hash = $"m4-ui-{index + 1:D2}";
    var original = Path.Combine(paths.Originals, $"{hash}.png");
    var small = Path.Combine(paths.SmallThumbnails, $"{hash}.png");
    var medium = Path.Combine(paths.MediumThumbnails, $"{hash}.png");
    SaveImage(original, colors[index], index);
    File.Copy(original, small, true);
    File.Copy(original, medium, true);
    var saved = await repository.SaveAsync(new SaveItemInput(
        new AssetInput(
            hash,
            paths.ToRelative(original),
            paths.ToRelative(small),
            paths.ToRelative(medium),
            960,
            640,
            "png"),
        prompts[index],
        "M4 synthetic UI validation",
        null,
        ["M4", "synthetic"]));
    await repository.UpsertImageEmbeddingAsync(
        saved.ItemId,
        "local-clip-vit-b32",
        "d15189d7028b43f1d3e65039190477f6af591c2a",
        vectors[index]);
    foreach (var metadata in new[]
             {
                 new AiMetadataValue("description", $"合成图片 {index + 1}，呈现极简、柔光特征", 0.78),
                 new AiMetadataValue("style", index == 1 ? "赛博朋克" : "极简", 0.74),
                 new AiMetadataValue("lighting", index == 2 ? "日光" : "柔光", 0.71),
                 new AiMetadataValue("color", index == 0 ? "柔和色彩" : "霓虹", 0.69)
             })
    {
        await repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            saved.ItemId,
            metadata.FieldType,
            metadata.Value,
            AiMetadataSource.LocalModel,
            "local-clip-vit-b32",
            "Xenova/clip-vit-base-patch32",
            "d15189d7028b43f1d3e65039190477f6af591c2a",
            metadata.Confidence));
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
{
    LibraryRoot = root,
    CaptureListeningEnabled = false,
    ReducedMotionEnabled = true
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { rootKind = "synthetic", items = 3, candidates = 12, captureListening = false }));
return 0;

static void SaveImage(string path, (int R, int G, int B) color, int variant)
{
    const int width = 960;
    const int height = 640;
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var offset = (y * width + x) * 4;
        var accent = ((x / 80 + y / 80 + variant) % 2 == 0) ? 18 : -12;
        pixels[offset] = (byte)Math.Clamp(color.B + accent, 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(color.G + accent, 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(color.R + accent, 0, 255);
        pixels[offset + 3] = 255;
    }
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}
