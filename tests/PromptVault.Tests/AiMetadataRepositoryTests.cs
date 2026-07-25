using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class AiMetadataRepositoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PromptVaultAiMetadataTests",
        Guid.NewGuid().ToString("N"));
    private LibraryRepository _repository = null!;
    private long _itemId;

    public async Task InitializeAsync()
    {
        _repository = new LibraryRepository(new LibraryPaths(_root));
        await _repository.InitializeAsync();
        _itemId = (await _repository.SaveAsync(new SaveItemInput(
            new AssetInput("ai-metadata-hash", "originals/a.jpg", "thumbs/a.jpg", "thumbs-medium/a.jpg", 100, 100, "jpeg"),
            "user prompt",
            "",
            null,
            []))).ItemId;
    }

    [Fact]
    public async Task CandidateTracksProvenanceAndConfirmationCreatesAuthoritativeMetadata()
    {
        var candidate = await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            _itemId,
            "description",
            "柔和日光下的极简产品",
            AiMetadataSource.LocalModel,
            "local-clip",
            "CLIP ViT-B/32",
            "d15189d",
            0.82));

        var confirmed = await _repository.ConfirmMetadataCandidateAsync(candidate.Id);
        var candidates = await _repository.GetMetadataCandidatesAsync(_itemId);

        Assert.Equal("柔和日光下的极简产品", confirmed.Value);
        Assert.Equal(candidate.Id, confirmed.SourceCandidateId);
        Assert.Equal(MetadataCandidateStatus.Confirmed, Assert.Single(candidates).Status);
        Assert.Equal(AiMetadataSource.LocalModel, candidates[0].Source);
        Assert.Equal(0.82, candidates[0].Confidence);
    }

    [Fact]
    public async Task ModelRerunCannotOverwriteConfirmedOrUserModifiedValue()
    {
        var candidate = await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            _itemId, "style", "极简", AiMetadataSource.LocalModel,
            "local-clip", "CLIP ViT-B/32", "v1", 0.7));
        await _repository.ConfirmMetadataCandidateAsync(candidate.Id, "用户修正的极简风格");

        var rerun = await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            _itemId, "style", "赛博朋克", AiMetadataSource.LocalModel,
            "local-clip", "CLIP ViT-B/32", "v1", 0.99));
        var authoritative = await _repository.GetUserMetadataAsync(_itemId, "style");

        Assert.Equal("极简", rerun.Value);
        Assert.Equal(MetadataCandidateStatus.Modified, rerun.Status);
        Assert.Equal("用户修正的极简风格", authoritative!.Value);
    }

    [Fact]
    public async Task RejectedCandidateRemainsRejectedOnRerun()
    {
        var candidate = await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            _itemId, "atmosphere", "压抑", AiMetadataSource.Rule,
            "rule-v1", "Metadata Rules", "1", 0.5));
        await _repository.RejectMetadataCandidateAsync(candidate.Id);

        var rerun = await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            _itemId, "atmosphere", "明快", AiMetadataSource.Rule,
            "rule-v1", "Metadata Rules", "1", 0.9));

        Assert.Equal(MetadataCandidateStatus.Rejected, rerun.Status);
        Assert.Equal("压抑", rerun.Value);
        Assert.Null(await _repository.GetUserMetadataAsync(_itemId, "atmosphere"));
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }
}
