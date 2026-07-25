using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class AiJobQueueTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PromptVaultAiJobTests",
        Guid.NewGuid().ToString("N"));
    private LibraryRepository _repository = null!;
    private long _itemId;

    public async Task InitializeAsync()
    {
        _repository = new LibraryRepository(new LibraryPaths(_root));
        await _repository.InitializeAsync();
        _itemId = (await _repository.SaveAsync(new SaveItemInput(
            new AssetInput("ai-job-hash", "originals/a.jpg", "thumbs/a.jpg", "thumbs-medium/a.jpg", 64, 64, "jpeg"),
            "prompt", "", null, []))).ItemId;
    }

    [Fact]
    public async Task QueueClaimsOnlyOneJobAndCachesByHashAndModelVersion()
    {
        var input = new AiJobInput(
            _itemId, "analyze", "local", "v1", "ai-job-hash:local:v1");
        var first = await _repository.EnqueueAiJobAsync(input);
        var duplicate = await _repository.EnqueueAiJobAsync(input with { Priority = 5 });
        var claimed = await _repository.ClaimNextAiJobAsync(DateTimeOffset.UtcNow);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.Id, claimed!.Id);
        Assert.Equal(1, claimed.Attempts);
        Assert.Null(await _repository.ClaimNextAiJobAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task QueuePausesResumesRetriesAndRecoversInterruptedWork()
    {
        var job = await _repository.EnqueueAiJobAsync(new AiJobInput(
            _itemId, "analyze", "local", "v1", "retry-cache", MaxAttempts: 2));
        Assert.Equal(1, await _repository.PauseAiJobsAsync());
        Assert.Null(await _repository.ClaimNextAiJobAsync(DateTimeOffset.UtcNow));
        Assert.Equal(1, await _repository.ResumeAiJobsAsync());

        var first = await _repository.ClaimNextAiJobAsync(DateTimeOffset.UtcNow);
        await _repository.FailAiJobAsync(first!.Id, "temporary", DateTimeOffset.UtcNow);
        var second = await _repository.ClaimNextAiJobAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        await _repository.FailAiJobAsync(second!.Id, "final", DateTimeOffset.UtcNow);

        var failed = Assert.Single(await _repository.GetAiJobsAsync(AiJobStatus.Failed));
        Assert.Equal(job.Id, failed.Id);
        Assert.Equal(2, failed.Attempts);

        var other = await _repository.EnqueueAiJobAsync(new AiJobInput(
            _itemId, "analyze", "local", "v2", "recover-cache"));
        await _repository.ClaimNextAiJobAsync(DateTimeOffset.UtcNow);
        Assert.Equal(1, await _repository.RecoverInterruptedAiJobsAsync());
        Assert.Equal(AiJobStatus.Queued,
            Assert.Single(await _repository.GetAiJobsAsync(), x => x.Id == other.Id).Status);
    }

    [Fact]
    public async Task ProcessorYieldsDuringInteractionThenPersistsDraftAndReleasesProvider()
    {
        await _repository.EnqueueAiJobAsync(new AiJobInput(
            _itemId, "analyze", "fake-local", "v1", "processor-cache"));
        var active = true;
        var provider = new FakeProvider();
        var processor = new AiJobProcessor(
            _repository,
            new AiProviderRegistry([provider]),
            (itemId, _) => Task.FromResult<AiProviderRequest?>(new AiProviderRequest(
                itemId, "synthetic.png", "", [], ["description"])),
            () => active);

        Assert.False(await processor.ProcessNextAsync());
        active = false;
        Assert.True(await processor.ProcessNextAsync());

        Assert.Equal(AiJobStatus.Completed,
            Assert.Single(await _repository.GetAiJobsAsync()).Status);
        Assert.Equal("本地草稿",
            Assert.Single(await _repository.GetMetadataCandidatesAsync(_itemId)).Value);
        Assert.Equal(1, provider.ReleaseCount);
    }

    [Fact]
    public async Task OnlineFailureLeavesItemAndUserMetadataUntouched()
    {
        await _repository.EnqueueAiJobAsync(new AiJobInput(
            _itemId,
            "analyze",
            "fake-online",
            "v1",
            "online-failure-cache",
            MaxAttempts: 1));
        var provider = new FailingOnlineProvider();
        var processor = new AiJobProcessor(
            _repository,
            new AiProviderRegistry([provider]),
            (itemId, _) => Task.FromResult<AiProviderRequest?>(new AiProviderRequest(
                itemId, "synthetic.png", "prompt", [], [])));

        Assert.True(await processor.ProcessNextAsync());

        var item = await _repository.GetGalleryItemAsync(_itemId);
        Assert.NotNull(item);
        Assert.Equal("prompt", item.Prompt);
        Assert.Empty(await _repository.GetMetadataCandidatesAsync(_itemId));
        Assert.Null(await _repository.GetUserMetadataAsync(_itemId, "description"));
        var failed = Assert.Single(await _repository.GetAiJobsAsync(AiJobStatus.Failed));
        Assert.Contains("synthetic online failure", failed.LastError, StringComparison.Ordinal);
    }

    private sealed class FakeProvider : IAiProvider
    {
        public AiProviderDescriptor Descriptor { get; } = new(
            "fake-local", "Fake Local", AiProviderKind.LocalVisionLanguage,
            "Fake", "v1", AiProviderCapabilities.Metadata, false);
        public int ReleaseCount { get; private set; }

        public Task<AiProviderResult> AnalyzeAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiProviderResult(
                [new AiMetadataValue("description", "本地草稿", 0.8)],
                null));

        public Task<float[]?> EmbedTextAsync(
            AiTextEmbeddingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<float[]?>(null);

        public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingOnlineProvider : IAiProvider
    {
        public AiProviderDescriptor Descriptor { get; } = new(
            "fake-online",
            "Fake Online",
            AiProviderKind.OnlineVision,
            "Fake",
            "v1",
            AiProviderCapabilities.Metadata,
            SendsDataOffDevice: true);

        public Task<AiProviderResult> AnalyzeAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("synthetic online failure");

        public Task<float[]?> EmbedTextAsync(
            AiTextEmbeddingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<float[]?>(null);

        public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }
}
