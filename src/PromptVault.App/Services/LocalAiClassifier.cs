using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PromptVault.Core;

namespace PromptVault.App.Services;

public sealed record AiSuggestion(IReadOnlyList<string> Categories, IReadOnlyList<string> Tags, bool UsedModel);

public sealed class LocalAiClassifier
{
    private readonly string _modelDirectory;
    public LocalAiClassifier(string modelDirectory) => _modelDirectory = modelDirectory;

    public Task<AiSuggestion> SuggestAsync(string imagePath, IReadOnlyList<CategoryRecord> categories, CancellationToken cancellationToken = default) =>
        Task.Run(() => Suggest(imagePath, categories, cancellationToken), cancellationToken);

    private AiSuggestion Suggest(string imagePath, IReadOnlyList<CategoryRecord> categories, CancellationToken cancellationToken)
    {
        var modelPath = Path.Combine(_modelDirectory, "clip", "image_encoder.onnx");
        var manifestPath = Path.Combine(_modelDirectory, "clip", "manifest.json");
        if (!File.Exists(modelPath) || !File.Exists(manifestPath)) return Fallback(imagePath, categories);

        try
        {
            var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("模型清单无效。");
            using var session = CreateSession(modelPath, manifest.PreferDirectMl);
            var tensor = BuildTensor(imagePath, manifest.ImageSize);
            cancellationToken.ThrowIfCancellationRequested();
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(manifest.InputName, tensor)]);
            var embedding = results.First(x => x.Name == manifest.OutputName).AsEnumerable<float>().ToArray();
            Normalize(embedding);

            var categoryScores = categories
                .Where(c => manifest.CategoryVectors.TryGetValue(c.Name, out var vector) && vector.Length == embedding.Length)
                .Select(c => (c.Name, Score: Cosine(embedding, manifest.CategoryVectors[c.Name])))
                .OrderByDescending(x => x.Score)
                .ToArray();
            var tagScores = manifest.TagVectors
                .Where(x => x.Value.Length == embedding.Length)
                .Select(x => (x.Key, Score: Cosine(embedding, x.Value)))
                .OrderByDescending(x => x.Score)
                .ToArray();

            if (ApplyAutoCategoryRules(embedding, categories, manifest, categoryScores) is { } ruledCategory)
            {
                return new AiSuggestion([ruledCategory], BuildTagSuggestions(tagScores, manifest, ruledCategory), true);
            }

            var guardedCategories = manifest.AutoCategoryRules.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var suggestions = categoryScores
                .Where(x => !guardedCategories.Contains(x.Name))
                .Take(3)
                .Select(x => x.Name)
                .ToArray();
            return new AiSuggestion(suggestions, BuildTagSuggestions(tagScores, manifest, null), true);
        }
        catch
        {
            return Fallback(imagePath, categories);
        }
    }

    private static string[] BuildTagSuggestions(
        IReadOnlyList<(string Key, float Score)> tagScores,
        ModelManifest manifest,
        string? selectedCategory)
    {
        var selectedRule = selectedCategory is null
            ? null
            : manifest.AutoCategoryRules.FirstOrDefault(x => x.Name.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase));
        var selectedTags = selectedRule is null
            ? []
            : SelectRuleTags(tagScores, selectedRule);
        var guardedTags = manifest.AutoCategoryRules
            .SelectMany(x => x.TagNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in selectedTags) guardedTags.Add(tag);

        return selectedTags
            .Concat(tagScores.Where(x => !guardedTags.Contains(x.Key)).Select(x => x.Key))
            .Take(5)
            .ToArray();
    }
    private static string[] SelectRuleTags(IReadOnlyList<(string Key, float Score)> tagScores, AutoCategoryRule rule)
    {
        if (rule.TagNames.Length == 0) return [];
        var candidates = tagScores
            .Where(x => rule.TagNames.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Score)
            .ToArray();
        if (candidates.Length == 0) return [];

        var best = candidates[0].Score;
        return candidates
            .Where((x, index) => index == 0 || best - x.Score <= rule.MaxTagScoreGap)
            .Take(rule.MaxTagCount)
            .Select(x => x.Key)
            .ToArray();
    }
    private static string? ApplyAutoCategoryRules(
        float[] embedding,
        IReadOnlyList<CategoryRecord> categories,
        ModelManifest manifest,
        IReadOnlyList<(string Name, float Score)> categoryScores)
    {
        if (manifest.AutoCategoryRules.Count == 0) return null;
        var available = categories.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in manifest.AutoCategoryRules)
        {
            if (!available.Contains(rule.Name)) continue;
            if (!manifest.CategoryVectors.TryGetValue(rule.Name, out var vector) || vector.Length != embedding.Length) continue;

            var score = Cosine(embedding, vector);
            var compareScore = BestCompareScore(embedding, manifest, categoryScores, rule);
            if (score >= rule.MinScore && score - compareScore >= rule.MinMargin)
            {
                return rule.Name;
            }
        }

        return null;
    }

    private static float BestCompareScore(
        float[] embedding,
        ModelManifest manifest,
        IReadOnlyList<(string Name, float Score)> categoryScores,
        AutoCategoryRule rule)
    {
        var best = float.NegativeInfinity;
        foreach (var name in rule.CompareWith)
        {
            if (manifest.GuardVectors.TryGetValue(name, out var guard) && guard.Length == embedding.Length)
            {
                best = Math.Max(best, Cosine(embedding, guard));
                continue;
            }

            var category = categoryScores.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(category.Name))
            {
                best = Math.Max(best, category.Score);
            }
        }

        if (!float.IsNegativeInfinity(best)) return best;
        return categoryScores.Where(x => !x.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Score)
            .DefaultIfEmpty(float.NegativeInfinity)
            .Max();
    }
    private static InferenceSession CreateSession(string modelPath, bool preferDirectMl)
    {
        if (!preferDirectMl) return new InferenceSession(modelPath);
        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            options.AppendExecutionProvider_DML(0);
            return new InferenceSession(modelPath, options, null);
        }
        catch
        {
            return new InferenceSession(modelPath);
        }
    }
    private static DenseTensor<float> BuildTensor(string imagePath, int size)
    {
        var frame = ImagePipeline.DecodeFirstFrame(imagePath);
        var scaled = new TransformedBitmap(frame, new ScaleTransform(size / (double)frame.PixelWidth, size / (double)frame.PixelHeight));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        bgra.CopyPixels(pixels, size * 4, 0);
        var tensor = new DenseTensor<float>([1, 3, size, size]);
        var mean = new[] { 0.48145466f, 0.4578275f, 0.40821073f };
        var std = new[] { 0.26862954f, 0.26130258f, 0.27577711f };
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var p = (y * size + x) * 4;
            tensor[0, 0, y, x] = (pixels[p + 2] / 255f - mean[0]) / std[0];
            tensor[0, 1, y, x] = (pixels[p + 1] / 255f - mean[1]) / std[1];
            tensor[0, 2, y, x] = (pixels[p] / 255f - mean[2]) / std[2];
        }
        return tensor;
    }

    private static AiSuggestion Fallback(string imagePath, IReadOnlyList<CategoryRecord> categories)
    {
        var name = Path.GetFileNameWithoutExtension(imagePath).ToLowerInvariant();
        var keywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["人物"] = ["portrait", "girl", "boy", "person", "character", "人物", "人像"],
            ["场景"] = ["landscape", "scene", "city", "nature", "场景", "风景"],
            ["产品"] = ["product", "packshot", "商品", "产品"],
            ["建筑"] = ["architecture", "interior", "building", "建筑", "室内"],
            ["插画"] = ["illustration", "anime", "art", "插画", "动漫"],
            ["界面设计"] = ["ui", "web", "app", "界面"]
        };
        var category = categories.FirstOrDefault(c => keywords.TryGetValue(c.Name, out var words) && words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        return new AiSuggestion(category is null ? [] : [category.Name], [], false);
    }

    private static float Cosine(float[] a, float[] b)
    {
        double sum = 0, norm = 0;
        for (var i = 0; i < a.Length; i++) { sum += a[i] * b[i]; norm += b[i] * b[i]; }
        return norm <= 0 ? -1 : (float)(sum / Math.Sqrt(norm));
    }

    private static void Normalize(float[] values)
    {
        var norm = Math.Sqrt(values.Sum(x => x * x));
        if (norm <= 0) return;
        for (var i = 0; i < values.Length; i++) values[i] /= (float)norm;
    }

    private sealed class ModelManifest
    {
        public int ImageSize { get; set; } = 224;
        public bool PreferDirectMl { get; set; }
        public string InputName { get; set; } = "pixel_values";
        public string OutputName { get; set; } = "image_embeds";
        public string CategorySuggestionMode { get; set; } = "ranked";
        public Dictionary<string, float[]> CategoryVectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float[]> GuardVectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float[]> TagVectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AutoCategoryRule> AutoCategoryRules { get; set; } = [];
    }

    private sealed class AutoCategoryRule
    {
        public string Name { get; set; } = "";
        public float MinScore { get; set; }
        public float MinMargin { get; set; }
        public string[] CompareWith { get; set; } = [];
        public string[] TagNames { get; set; } = [];
        public int MaxTagCount { get; set; } = 2;
        public float MaxTagScoreGap { get; set; } = 0.02f;
    }
}
