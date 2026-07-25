namespace PromptVault.Core;

[Flags]
public enum AiProviderCapabilities
{
    None = 0,
    ImageEmbedding = 1,
    TextEmbedding = 2,
    Metadata = 4,
    ChineseDescription = 8
}

public enum AiProviderKind
{
    LocalFastVector,
    LocalVisionLanguage,
    OnlineVision
}

public sealed record AiProviderDescriptor(
    string Id,
    string DisplayName,
    AiProviderKind Kind,
    string ModelName,
    string ModelVersion,
    AiProviderCapabilities Capabilities,
    bool SendsDataOffDevice);

public sealed record AiProviderRequest(
    long ItemId,
    string ImagePath,
    string ExistingPrompt,
    IReadOnlyList<CategoryRecord> Categories,
    IReadOnlyList<string> RequestedFields);

public sealed record AiTextEmbeddingRequest(string Text);

public sealed record AiMetadataValue(
    string FieldType,
    string Value,
    double? Confidence);

public sealed record AiProviderResult(
    IReadOnlyList<AiMetadataValue> Metadata,
    float[]? ImageEmbedding,
    string? DiagnosticSummary = null);

public interface IAiProvider : IAsyncDisposable
{
    AiProviderDescriptor Descriptor { get; }

    Task<AiProviderResult> AnalyzeAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken = default);

    Task<float[]?> EmbedTextAsync(
        AiTextEmbeddingRequest request,
        CancellationToken cancellationToken = default);

    Task ReleaseResourcesAsync(CancellationToken cancellationToken = default);
}

public sealed class AiProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IAiProvider> _providers;

    public AiProviderRegistry(IEnumerable<IAiProvider> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.Descriptor.Id,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<AiProviderDescriptor> Descriptors =>
        _providers.Values.Select(provider => provider.Descriptor).ToArray();

    public IAiProvider GetRequired(string providerId) =>
        _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new KeyNotFoundException($"找不到 AI Provider：{providerId}");

    public IAiProvider? FindFirst(AiProviderCapabilities capability, bool allowOnline) =>
        _providers.Values
            .Where(provider =>
                allowOnline || !provider.Descriptor.SendsDataOffDevice)
            .FirstOrDefault(provider =>
                (provider.Descriptor.Capabilities & capability) == capability);
}
