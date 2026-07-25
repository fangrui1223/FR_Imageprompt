namespace PromptVault.Core;

public sealed class SimilaritySearchService
{
    private readonly LibraryRepository _repository;
    private readonly AiProviderRegistry _providers;

    public SimilaritySearchService(LibraryRepository repository, AiProviderRegistry providers)
    {
        _repository = repository;
        _providers = providers;
    }

    public async Task<IReadOnlyList<SimilarityMatch>> SearchByImageAsync(
        long itemId,
        string providerId,
        string modelVersion,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var embeddings = await _repository.LoadImageEmbeddingsAsync(
            providerId, modelVersion, cancellationToken).ConfigureAwait(false);
        var source = embeddings.FirstOrDefault(entry => entry.ItemId == itemId)
            ?? throw new KeyNotFoundException("所选图片还没有可用向量。");
        return new SimilarityIndex(embeddings).Search(source.Vector, limit, itemId);
    }

    public async Task<IReadOnlyList<SimilarityMatch>> SearchByTextAsync(
        string text,
        string providerId,
        string modelVersion,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var provider = _providers.GetRequired(providerId);
        var query = await provider.EmbedTextAsync(
            new AiTextEmbeddingRequest(text.Trim()),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("当前 Provider 不支持文本向量。");
        var embeddings = await _repository.LoadImageEmbeddingsAsync(
            providerId, modelVersion, cancellationToken).ConfigureAwait(false);
        return new SimilarityIndex(embeddings).Search(query, limit);
    }
}
