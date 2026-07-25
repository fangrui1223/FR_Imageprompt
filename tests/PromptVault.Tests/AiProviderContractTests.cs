using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class AiProviderContractTests
{
    [Fact]
    public void RegistrySelectsCapabilityWithoutSilentlyUsingOnlineProvider()
    {
        var online = new FakeProvider(
            "online",
            AiProviderKind.OnlineVision,
            AiProviderCapabilities.Metadata,
            sendsDataOffDevice: true);
        var local = new FakeProvider(
            "local",
            AiProviderKind.LocalVisionLanguage,
            AiProviderCapabilities.Metadata | AiProviderCapabilities.ImageEmbedding,
            sendsDataOffDevice: false);
        var registry = new AiProviderRegistry([online, local]);

        Assert.Same(local, registry.FindFirst(AiProviderCapabilities.Metadata, allowOnline: false));
        Assert.Same(local, registry.GetRequired("LOCAL"));
        Assert.All(registry.Descriptors.Where(x => x.Kind == AiProviderKind.OnlineVision),
            descriptor => Assert.True(descriptor.SendsDataOffDevice));
    }

    [Fact]
    public async Task ProviderContractReturnsMetadataAndNormalizedEmbeddingShape()
    {
        await using var provider = new FakeProvider(
            "local",
            AiProviderKind.LocalFastVector,
            AiProviderCapabilities.Metadata | AiProviderCapabilities.ImageEmbedding,
            sendsDataOffDevice: false);
        var result = await provider.AnalyzeAsync(new AiProviderRequest(
            7,
            "synthetic.png",
            "",
            [],
            ["description"]));

        Assert.Equal("description", Assert.Single(result.Metadata).FieldType);
        Assert.Equal(4, result.ImageEmbedding!.Length);
        Assert.False(provider.Descriptor.SendsDataOffDevice);
    }

    private sealed class FakeProvider : IAiProvider
    {
        public FakeProvider(
            string id,
            AiProviderKind kind,
            AiProviderCapabilities capabilities,
            bool sendsDataOffDevice)
        {
            Descriptor = new AiProviderDescriptor(
                id,
                id,
                kind,
                "fake",
                "1",
                capabilities,
                sendsDataOffDevice);
        }

        public AiProviderDescriptor Descriptor { get; }

        public Task<AiProviderResult> AnalyzeAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiProviderResult(
                [new AiMetadataValue("description", "synthetic", 0.5)],
                [1f, 0f, 0f, 0f]));

        public Task<float[]?> EmbedTextAsync(
            AiTextEmbeddingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<float[]?>([1f, 0f, 0f, 0f]);

        public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
