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

    [Fact]
    public async Task ManualOrganizationRejectsOnlyConflictingPendingCandidates()
    {
        var inputs = new[]
        {
            ("category", "裤子参考"),
            ("tags", "阔腿裤"),
            ("description", "AI 描述"),
            ("style", "极简")
        };
        foreach (var (field, value) in inputs)
        {
            await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
                _itemId,
                field,
                value,
                AiMetadataSource.LocalModel,
                "local-clip",
                "CLIP ViT-B/32",
                "v1",
                0.8));
        }

        var categoryId = await _repository.GetOrCreateCategoryAsync("上衣参考", "人工分类");
        await _repository.UpdateItemOrganizationAsync(
            _itemId,
            "user prompt",
            "针织, 短款",
            "人工备注",
            categoryId);

        var item = await _repository.GetGalleryItemAsync(_itemId);
        var candidates = await _repository.GetMetadataCandidatesAsync(_itemId);

        Assert.Equal(categoryId, item!.CategoryId);
        Assert.Contains("针织", item.Tags);
        Assert.Equal("人工备注", item.Notes);
        Assert.All(
            candidates.Where(candidate => candidate.FieldType is "category" or "tags" or "description"),
            candidate => Assert.Equal(MetadataCandidateStatus.Rejected, candidate.Status));
        Assert.Equal(
            MetadataCandidateStatus.Pending,
            Assert.Single(candidates, candidate => candidate.FieldType == "style").Status);
    }

    [Fact]
    public async Task ConfirmedCategoryCandidateMapsOnlyToAnExistingCategory()
    {
        var categoryId = await _repository.GetOrCreateCategoryAsync("裤子参考", "人工精确分类");
        var mapped = await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            _itemId,
            "category",
            "裤子参考",
            AiMetadataSource.LocalModel,
            "local-clip",
            "CLIP ViT-B/32",
            "v1",
            0.8));

        await _repository.ConfirmMetadataCandidateAsync(mapped.Id);

        Assert.Equal(categoryId, (await _repository.GetGalleryItemAsync(_itemId))!.CategoryId);

        var unknown = await _repository.UpsertMetadataCandidateAsync(new MetadataCandidateInput(
            _itemId,
            "category",
            "含糊的新分类",
            AiMetadataSource.OnlineApi,
            "fake-online",
            "Fake",
            "v2",
            0.6));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.ConfirmMetadataCandidateAsync(unknown.Id));

        Assert.Equal(
            MetadataCandidateStatus.Pending,
            Assert.Single(
                await _repository.GetMetadataCandidatesAsync(_itemId),
                candidate => candidate.Id == unknown.Id).Status);
        Assert.Equal(categoryId, (await _repository.GetGalleryItemAsync(_itemId))!.CategoryId);
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }
}
