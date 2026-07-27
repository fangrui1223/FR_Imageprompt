using PromptVault.Core;

namespace PromptVault.App.Services;

public static class AiWorkerHost
{
    public static async Task<int> RunOnceAsync(
        string libraryRoot,
        AppSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var repository = new LibraryRepository(new LibraryPaths(libraryRoot));
        await repository.InitializeAsync(
            settings is null ? null : new LibraryUpgradeOptions(settings.StorageFilePath),
            cancellationToken).ConfigureAwait(false);
        await repository.RecoverInterruptedAiJobsAsync(cancellationToken).ConfigureAwait(false);
        while (IsUiActive(repository.Paths.Root))
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        var providers = new List<IAiProvider>
        {
            new LocalClipAiProvider(repository.Paths.Models)
        };
        if (settings is { OnlineAiEnabled: true }
            && OnlineAiConfiguration.TryCreate(settings, out var onlineOptions, out _)
            && onlineOptions is not null
            && WindowsCredentialStore.HasOnlineAiKey())
        {
            providers.Add(new OpenAiCompatibleVisionProvider(
                onlineOptions,
                () => WindowsCredentialStore.TryReadOnlineAiKey(out var key) ? key : null));
        }
        var categories = await repository.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
        var processor = new AiJobProcessor(
            repository,
            new AiProviderRegistry(providers),
            async (itemId, token) =>
            {
                var item = await repository.GetGalleryItemAsync(itemId, token).ConfigureAwait(false);
                return item is null
                    ? null
                    : new AiProviderRequest(
                        item.Id,
                        repository.Paths.ToAbsolute(item.OriginalPath),
                        item.Prompt,
                        categories,
                        []);
            },
            () => IsUiActive(repository.Paths.Root));
        try
        {
            var processed = 0;
            while (!cancellationToken.IsCancellationRequested
                   && await processor.ProcessNextAsync(cancellationToken).ConfigureAwait(false))
            {
                processed++;
            }
            return processed;
        }
        finally
        {
            foreach (var provider in providers)
                await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsUiActive(string libraryRoot)
    {
        var marker = Path.Combine(libraryRoot, ".ai-ui-active");
        return File.Exists(marker)
               && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromSeconds(2);
    }
}
