using Microsoft.Data.Sqlite;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class CaptureRepositoryTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "PromptVaultCaptureTests", Guid.NewGuid().ToString("N"));
    private LibraryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = new LibraryRepository(new LibraryPaths(_root));
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CaptureLifecyclePersistsAcrossRepositoryRestart()
    {
        var capturedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var id = Guid.NewGuid();
        await _repository.CreateCaptureSessionAsync(new CaptureSessionInput(
            id,
            ".staging/original.png",
            "png",
            capturedAt,
            capturedAt.AddMinutes(2),
            42));
        await _repository.TransitionCaptureAsync(id, CaptureState.PreparingImage);
        await _repository.MarkCapturePreparedAsync(
            id,
            new PreparedCaptureInput(
                ".staging/small.jpg",
                ".staging/medium.jpg",
                "capture-hash",
                "png",
                1024,
                768),
            DateTimeOffset.UtcNow);
        await _repository.SetCapturePromptAsync(id, "stable prompt", 43);

        SqliteConnection.ClearAllPools();
        var reopened = new LibraryRepository(new LibraryPaths(_root));
        await reopened.InitializeAsync();
        var restored = Assert.Single(await reopened.GetActiveCaptureSessionsAsync(DateTimeOffset.UtcNow));

        Assert.Equal(id, restored.Id);
        Assert.Equal(CaptureState.PromptDebouncing, restored.State);
        Assert.Equal("stable prompt", restored.Prompt);
        Assert.Equal("capture-hash", restored.Hash);
        Assert.Equal(43u, restored.LastClipboardSequence);
        Assert.Equal(1024, restored.Width);
        Assert.Equal(768, restored.Height);
    }

    [Fact]
    public async Task ExpiredPreparedCaptureEntersNeedsPrompt()
    {
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var id = Guid.NewGuid();
        await _repository.CreateCaptureSessionAsync(new CaptureSessionInput(
            id,
            ".staging/expired.png",
            "png",
            capturedAt,
            capturedAt.AddMinutes(2)));
        await _repository.TransitionCaptureAsync(id, CaptureState.PreparingImage);
        var prepared = await _repository.MarkCapturePreparedAsync(
            id,
            new PreparedCaptureInput(
                ".staging/expired-small.jpg",
                ".staging/expired-medium.jpg",
                "expired-hash",
                "png",
                100,
                100),
            DateTimeOffset.UtcNow);

        Assert.Equal(CaptureState.NeedsPrompt, prepared.State);
        Assert.Equal(id, Assert.Single(await _repository.GetCaptureInboxAsync()).Id);
    }

    [Fact]
    public async Task InvalidPersistentTransitionDoesNotChangeStoredState()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await _repository.CreateCaptureSessionAsync(new CaptureSessionInput(
            id,
            ".staging/invalid.png",
            "png",
            now,
            now.AddMinutes(2)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.TransitionCaptureAsync(id, CaptureState.Saved));

        Assert.Equal(
            CaptureState.ImageDetected,
            (await _repository.GetCaptureSessionAsync(id))!.State);
    }

    [Fact]
    public async Task UndoNewCaptureRemovesDatabaseItemAndItsThreeFiles()
    {
        var id = await CreatePreparedPromptCaptureAsync("undo-new-hash", "new prompt");
        var asset = CreateAsset("undo-new-hash", createFiles: true);
        var saved = await _repository.SaveCaptureAsync(
            id,
            new SaveItemInput(asset, "new prompt", "", null, []),
            DateTimeOffset.UtcNow.AddSeconds(10));

        var undo = await _repository.UndoCaptureAsync(id, DateTimeOffset.UtcNow);

        Assert.False(undo.RestoredDuplicate);
        Assert.Equal(saved.ItemId, undo.ItemId);
        Assert.Empty(undo.FileDeletionFailures);
        Assert.Null(await _repository.FindByHashAsync("undo-new-hash"));
        Assert.All(
            new[]
            {
                asset.OriginalPath,
                asset.ThumbnailPath,
                asset.MediumThumbnailPath
            },
            relative => Assert.False(File.Exists(_repository.Paths.ToAbsolute(relative))));
        Assert.Equal(
            CaptureState.Undone,
            (await _repository.GetCaptureSessionAsync(id))!.State);
    }

    [Fact]
    public async Task UndoDuplicateCaptureRestoresPreviousMetadataWithoutDeletingAsset()
    {
        var asset = CreateAsset("undo-duplicate-hash", createFiles: true);
        var original = await _repository.SaveAsync(new SaveItemInput(
            asset,
            "original duplicate prompt",
            "original note",
            null,
            ["original-tag"]));
        var id = await CreatePreparedPromptCaptureAsync(
            "undo-duplicate-hash",
            "replacement prompt");
        var saved = await _repository.SaveCaptureAsync(
            id,
            new SaveItemInput(
                asset,
                "replacement prompt",
                "replacement note",
                null,
                ["replacement-tag"]),
            DateTimeOffset.UtcNow.AddSeconds(10));

        Assert.True(saved.WasDuplicate);
        var undo = await _repository.UndoCaptureAsync(id, DateTimeOffset.UtcNow);

        Assert.True(undo.RestoredDuplicate);
        Assert.Equal(original.ItemId, undo.ItemId);
        var restored = Assert.Single((await _repository.SearchPageAsync(
            new SearchOptions(Query: "original duplicate", PageSize: 10))).Items);
        Assert.Equal("original note", restored.Notes);
        Assert.Contains("original-tag", restored.Tags);
        Assert.True(File.Exists(_repository.Paths.ToAbsolute(asset.OriginalPath)));
        Assert.True(File.Exists(_repository.Paths.ToAbsolute(asset.ThumbnailPath)));
        Assert.True(File.Exists(_repository.Paths.ToAbsolute(asset.MediumThumbnailPath)));
    }

    [Fact]
    public async Task UndoAfterDeadlineIsRejectedAndSavedItemRemains()
    {
        var id = await CreatePreparedPromptCaptureAsync("undo-expired-hash", "kept prompt");
        var asset = CreateAsset("undo-expired-hash", createFiles: false);
        await _repository.SaveCaptureAsync(
            id,
            new SaveItemInput(asset, "kept prompt", "", null, []),
            DateTimeOffset.UtcNow.AddSeconds(-1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.UndoCaptureAsync(id, DateTimeOffset.UtcNow));

        Assert.NotNull(await _repository.FindByHashAsync("undo-expired-hash"));
        Assert.Equal(
            CaptureState.Saved,
            (await _repository.GetCaptureSessionAsync(id))!.State);
    }

    [Fact]
    public async Task InboxItemsCanBeSupplementedAndSavedAsBatchAfterRestart()
    {
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-3).AddSeconds(index);
            var id = Guid.NewGuid();
            ids.Add(id);
            await _repository.CreateCaptureSessionAsync(new CaptureSessionInput(
                id,
                $".staging/{id:N}.png",
                "png",
                capturedAt,
                capturedAt.AddMinutes(2)));
            await _repository.TransitionCaptureAsync(id, CaptureState.PreparingImage);
            var prepared = await _repository.MarkCapturePreparedAsync(
                id,
                new PreparedCaptureInput(
                    $".staging/{id:N}.small.jpg",
                    $".staging/{id:N}.medium.jpg",
                    $"inbox-batch-hash-{index}",
                    "png",
                    640 + index,
                    480 + index),
                DateTimeOffset.UtcNow);
            Assert.Equal(CaptureState.NeedsPrompt, prepared.State);
        }

        SqliteConnection.ClearAllPools();
        var reopened = new LibraryRepository(new LibraryPaths(_root));
        await reopened.InitializeAsync();
        var restored = await reopened.GetCaptureInboxAsync();
        Assert.Equal(ids, restored.Select(item => item.Id));

        for (var index = 0; index < restored.Count; index++)
        {
            var capture = await reopened.SetCapturePromptAsync(
                restored[index].Id,
                $"supplemented prompt {index}");
            var result = await reopened.SaveCaptureAsync(
                capture.Id,
                new SaveItemInput(
                    CreateAsset($"inbox-batch-hash-{index}", createFiles: false),
                    capture.Prompt,
                    "",
                    null,
                    []),
                DateTimeOffset.UtcNow.AddSeconds(10));
            Assert.False(result.WasDuplicate);
        }

        Assert.Empty(await reopened.GetCaptureInboxAsync());
        Assert.Equal(
            3,
            (await reopened.SearchPageAsync(
                new SearchOptions(Query: "supplemented prompt", PageSize: 10))).TotalCount);
    }

    [Fact]
    public async Task AiSummaryCanExpireWithoutChangingSavedStateOrUndoDeadline()
    {
        var id = await CreatePreparedPromptCaptureAsync("ai-summary-hash", "prompt");
        var undoDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        await _repository.SaveCaptureAsync(
            id,
            new SaveItemInput(
                CreateAsset("ai-summary-hash", createFiles: false),
                "prompt",
                "",
                null,
                []),
            undoDeadline);

        var summarized = await _repository.SetCaptureAiSummaryAsync(id, "AI draft ready");
        var cleared = await _repository.ClearCaptureAiSummaryAsync(id);

        Assert.Equal(CaptureState.Saved, summarized.State);
        Assert.Equal("AI draft ready", summarized.AiSummary);
        Assert.Equal(CaptureState.Saved, cleared.State);
        Assert.Null(cleared.AiSummary);
        Assert.Equal(undoDeadline, cleared.UndoDeadlineAt);
    }

    private async Task<Guid> CreatePreparedPromptCaptureAsync(string hash, string prompt)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await _repository.CreateCaptureSessionAsync(new CaptureSessionInput(
            id,
            $".staging/{id:N}.png",
            "png",
            now,
            now.AddMinutes(2)));
        await _repository.TransitionCaptureAsync(id, CaptureState.PreparingImage);
        await _repository.MarkCapturePreparedAsync(
            id,
            new PreparedCaptureInput(
                $".staging/{id:N}.small.jpg",
                $".staging/{id:N}.medium.jpg",
                hash,
                "png",
                640,
                480),
            now);
        await _repository.SetCapturePromptAsync(id, prompt);
        return id;
    }

    private AssetInput CreateAsset(string hash, bool createFiles)
    {
        var asset = new AssetInput(
            hash,
            $"originals/{hash}.png",
            $"thumbnails/small/{hash}.jpg",
            $"thumbnails/medium/{hash}.jpg",
            640,
            480,
            "png");
        if (!createFiles) return asset;
        foreach (var relative in new[]
                 {
                     asset.OriginalPath,
                     asset.ThumbnailPath,
                     asset.MediumThumbnailPath
                 })
        {
            var path = _repository.Paths.ToAbsolute(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic");
        }
        return asset;
    }
}
