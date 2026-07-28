namespace PromptVault.Core;

public static class AiMetadataAuthority
{
    public static bool IsCandidateBlocked(GalleryItem? item, string fieldType)
    {
        if (item is null) return false;
        return fieldType.Trim().ToLowerInvariant() switch
        {
            "category" => item.CategoryId is not null,
            "tags" => !string.IsNullOrWhiteSpace(item.Tags),
            "description" => !string.IsNullOrWhiteSpace(item.Notes),
            _ => false
        };
    }

    public static IReadOnlyList<string> SupersededCandidateFields(
        long? categoryId,
        bool hasTags,
        string notes)
    {
        var fields = new List<string>(3);
        if (categoryId is not null) fields.Add("category");
        if (hasTags) fields.Add("tags");
        if (!string.IsNullOrWhiteSpace(notes)) fields.Add("description");
        return fields;
    }
}

public sealed class AiJobProcessor
{
    private readonly LibraryRepository _repository;
    private readonly AiProviderRegistry _providers;
    private readonly Func<long, CancellationToken, Task<AiProviderRequest?>> _requestFactory;
    private readonly Func<bool> _shouldYield;

    public AiJobProcessor(
        LibraryRepository repository,
        AiProviderRegistry providers,
        Func<long, CancellationToken, Task<AiProviderRequest?>> requestFactory,
        Func<bool>? shouldYield = null)
    {
        _repository = repository;
        _providers = providers;
        _requestFactory = requestFactory;
        _shouldYield = shouldYield ?? (() => false);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (_shouldYield()) return false;
        var job = await _repository.ClaimNextAiJobAsync(
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (job is null) return false;

        try
        {
            var request = await _requestFactory(job.ItemId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI 任务对应的图片不存在。");
            var provider = _providers.GetRequired(job.ProviderId);
            var result = await provider.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
            var authoritative = await _repository.GetGalleryItemAsync(
                job.ItemId,
                cancellationToken).ConfigureAwait(false);
            foreach (var metadata in result.Metadata)
            {
                if (AiMetadataAuthority.IsCandidateBlocked(authoritative, metadata.FieldType))
                {
                    continue;
                }
                await _repository.UpsertMetadataCandidateAsync(
                    new MetadataCandidateInput(
                        job.ItemId,
                        metadata.FieldType,
                        metadata.Value,
                        provider.Descriptor.Kind == AiProviderKind.OnlineVision
                            ? AiMetadataSource.OnlineApi
                            : AiMetadataSource.LocalModel,
                        provider.Descriptor.Id,
                        provider.Descriptor.ModelName,
                        provider.Descriptor.ModelVersion,
                        metadata.Confidence),
                    cancellationToken).ConfigureAwait(false);
            }
            if (result.ImageEmbedding is { Length: > 0 } embedding)
            {
                await _repository.UpsertImageEmbeddingAsync(
                    job.ItemId,
                    provider.Descriptor.Id,
                    provider.Descriptor.ModelVersion,
                    embedding,
                    cancellationToken).ConfigureAwait(false);
            }
            await _repository.CompleteAiJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
            await provider.ReleaseResourcesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _repository.FailAiJobAsync(
                job.Id,
                "Worker cancelled.",
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, job.Attempts)));
            await _repository.FailAiJobAsync(
                job.Id,
                ex.Message,
                DateTimeOffset.UtcNow + delay,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

}
