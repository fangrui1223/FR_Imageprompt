using System.Text.Json;
using PromptVault.Core;

namespace PromptVault.App.Services;

public sealed class LocalClipAiProvider : IAiProvider
{
    public const string ProviderId = "local-clip-vit-b32";
    public const string ModelVersion = "d15189d7028b43f1d3e65039190477f6af591c2a";
    private readonly string _modelDirectory;
    private readonly LocalAiClassifier _classifier;

    public LocalClipAiProvider(string modelDirectory)
    {
        _modelDirectory = modelDirectory;
        _classifier = new LocalAiClassifier(modelDirectory);
    }

    public AiProviderDescriptor Descriptor { get; } = new(
        ProviderId,
        "本地 CLIP ViT-B/32",
        AiProviderKind.LocalVisionLanguage,
        "Xenova/clip-vit-base-patch32",
        ModelVersion,
        AiProviderCapabilities.ImageEmbedding
        | AiProviderCapabilities.TextEmbedding
        | AiProviderCapabilities.Metadata
        | AiProviderCapabilities.ChineseDescription,
        SendsDataOffDevice: false);

    public async Task<AiProviderResult> AnalyzeAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await _classifier.SuggestAsync(
            request.ImagePath,
            request.Categories,
            cancellationToken).ConfigureAwait(false);
        if (!suggestion.UsedModel || suggestion.Embedding is null)
        {
            throw new InvalidOperationException("本地 CLIP 模型包尚未安装或无法加载。");
        }

        var values = BuildMetadata(suggestion)
            .Where(value =>
                request.RequestedFields.Count == 0
                || request.RequestedFields.Contains(value.FieldType, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return new AiProviderResult(values, suggestion.Embedding, "Local inference completed.");
    }

    public async Task<float[]?> EmbedTextAsync(
        AiTextEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_modelDirectory, "clip", "manifest.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var vectors = new List<float[]>();
        foreach (var propertyName in new[] { "categoryVectors", "tagVectors", "guardVectors" })
        {
            if (!root.TryGetProperty(propertyName, out var group)) continue;
            foreach (var property in group.EnumerateObject())
            {
                if (!request.Text.Contains(property.Name, StringComparison.OrdinalIgnoreCase)) continue;
                vectors.Add(property.Value.EnumerateArray().Select(value => value.GetSingle()).ToArray());
            }
        }
        if (vectors.Count == 0) return null;
        var average = new float[vectors[0].Length];
        foreach (var vector in vectors)
        for (var index = 0; index < average.Length; index++)
            average[index] += vector[index] / vectors.Count;
        SimilarityIndex.NormalizeInPlace(average);
        return average;
    }

    public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IReadOnlyList<AiMetadataValue> BuildMetadata(AiSuggestion suggestion)
    {
        var confidence = suggestion.Confidence;
        var category = suggestion.Categories.FirstOrDefault();
        var tags = suggestion.Tags.ToArray();
        var values = new List<AiMetadataValue>();
        if (!string.IsNullOrWhiteSpace(category))
            values.Add(new AiMetadataValue("category", category, confidence));
        if (tags.Length > 0)
            values.Add(new AiMetadataValue("tags", string.Join(", ", tags), confidence));
        values.Add(new AiMetadataValue(
            "description",
            BuildDescription(category, tags),
            confidence));
        AddAttribute(values, "style", tags,
            ["写实", "电影感", "赛博朋克", "科幻", "奇幻", "极简", "复古", "动漫", "水彩", "油画", "3D渲染", "概念艺术"],
            confidence);
        AddAttribute(values, "lighting", tags,
            ["日光", "夜景", "工作室灯光", "柔光", "戏剧光影", "霓虹"],
            confidence);
        AddAttribute(values, "color", tags,
            ["黑白", "霓虹", "柔和色彩"],
            confidence);
        AddAttribute(values, "composition", tags,
            ["全身", "特写", "广角", "航拍", "微距"],
            confidence);
        AddAttribute(values, "texture", tags,
            ["水彩", "油画", "3D渲染", "写实"],
            confidence);
        AddAttribute(values, "atmosphere", tags,
            ["电影感", "赛博朋克", "科幻", "奇幻", "复古", "夜景", "柔和色彩"],
            confidence);
        return values;
    }

    private static string BuildDescription(string? category, IReadOnlyList<string> tags)
    {
        var subject = string.IsNullOrWhiteSpace(category) ? "图片" : $"{category}参考图";
        return tags.Count == 0
            ? subject
            : $"{subject}，呈现{string.Join("、", tags.Take(3))}特征";
    }

    private static void AddAttribute(
        ICollection<AiMetadataValue> values,
        string field,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> candidates,
        double? confidence)
    {
        var match = tags.FirstOrDefault(tag => candidates.Contains(tag, StringComparer.OrdinalIgnoreCase));
        if (match is not null) values.Add(new AiMetadataValue(field, match, confidence));
    }
}
