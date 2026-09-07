using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class CaptureCoordinatorRecoveryTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "PromptVaultCaptureRecoveryTests", Guid.NewGuid().ToString("N"));
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
    public async Task PreparedCaptureAndPreviewRecoverAfterRepositoryRestart()
    {
        var source = Path.Combine(_root, "synthetic-source.png");
        WriteSyntheticPng(source, 96, 72, 17);
        var coordinator = new CaptureCoordinator(_repository);
        var original = await coordinator.CreateFromFileAsync(source, 91);
        var before = await _repository.GetCaptureSessionAsync(original.SessionId);
        Assert.Equal(CaptureState.WaitingForPrompt, before!.State);

        SqliteConnection.ClearAllPools();
        var reopened = new LibraryRepository(new LibraryPaths(_root));
        await reopened.InitializeAsync();
        var restored = Assert.Single(
            await reopened.GetActiveCaptureSessionsAsync(DateTimeOffset.UtcNow));
        var recovered = await new CaptureCoordinator(reopened).RecoverAsync(restored);

        Assert.NotNull(recovered);
        Assert.Equal(original.SessionId, recovered.SessionId);
        Assert.Equal(96, recovered.Width);
        Assert.Equal(72, recovered.Height);
        Assert.Equal(original.Hash, recovered.Hash);
        Assert.True(File.Exists(recovered.StagedOriginal));
        Assert.True(File.Exists(recovered.StagedSmall));
        Assert.True(File.Exists(recovered.StagedMedium));

        await new CaptureCoordinator(reopened).DiscardAsync(restored, recovered);
        Assert.Equal(
            CaptureState.Undone,
            (await reopened.GetCaptureSessionAsync(restored.Id))!.State);
    }

    [Fact]
    public async Task DiscardingInboxItemDeletesOnlyItsStagingFiles()
    {
        var source = Path.Combine(_root, "discard-source.png");
        WriteSyntheticPng(source, 80, 60, 23);
        var coordinator = new CaptureCoordinator(_repository);
        var pending = await coordinator.CreateFromFileAsync(source);
        var inboxSession = await _repository.TransitionCaptureAsync(
            pending.SessionId,
            CaptureState.NeedsPrompt);
        var stagingFiles = new[]
        {
            pending.StagedOriginal,
            pending.StagedSmall,
            pending.StagedMedium
        };

        await coordinator.DiscardAsync(inboxSession, pending);

        Assert.True(File.Exists(source));
        Assert.All(stagingFiles, path => Assert.False(File.Exists(path)));
        Assert.Equal(
            CaptureState.Undone,
            (await _repository.GetCaptureSessionAsync(pending.SessionId))!.State);
        Assert.Empty(await _repository.GetCaptureInboxAsync());
        Assert.Equal(
            0,
            (await _repository.SearchPageAsync(
                new SearchOptions(PageSize: 10))).TotalCount);
    }

    [Fact]
    public async Task CorruptImagePersistsFriendlyFailureForInboxRecovery()
    {
        var source = Path.Combine(_root, "corrupt.png");
        await File.WriteAllTextAsync(source, "not an image");
        var coordinator = new CaptureCoordinator(_repository);

        var exception = await Assert.ThrowsAsync<CapturePreparationException>(() =>
            coordinator.CreateFromFileAsync(source));
        var failed = await _repository.GetCaptureSessionAsync(exception.CaptureId);

        Assert.NotNull(failed);
        Assert.Equal(CaptureState.Failed, failed.State);
        Assert.Contains("损坏", failed.Error);
        var stagedOriginal = _repository.Paths.ToAbsolute(failed.StagedOriginalPath);
        Assert.True(File.Exists(stagedOriginal));
        Assert.Equal(exception.CaptureId, Assert.Single(await _repository.GetCaptureInboxAsync()).Id);

        await coordinator.DiscardAsync(failed);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(stagedOriginal));
    }

    [Fact]
    public async Task LockedSourceFailsWithoutCreatingRecoverableSession()
    {
        var source = Path.Combine(_root, "locked.png");
        WriteSyntheticPng(source, 48, 48, 31);
        await using var locked = new FileStream(
            source,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var coordinator = new CaptureCoordinator(_repository);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.CreateFromFileAsync(source));

        Assert.Empty(await _repository.GetCaptureInboxAsync());
        Assert.Empty(await _repository.GetActiveCaptureSessionsAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SaveSurvivesRestartWithoutModelOrAiWorker()
    {
        var source = Path.Combine(_root, "no-ai-source.png");
        WriteSyntheticPng(source, 120, 90, 43);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_repository.Paths.Models));
        var coordinator = new CaptureCoordinator(_repository);
        var pending = await coordinator.CreateFromFileAsync(source);
        var prompted = await _repository.SetCapturePromptAsync(
            pending.SessionId,
            "saved without AI");

        var saved = await coordinator.SaveAsync(
            pending,
            prompted.Prompt,
            "",
            null,
            []);

        SqliteConnection.ClearAllPools();
        var reopened = new LibraryRepository(new LibraryPaths(_root));
        await reopened.InitializeAsync();
        var restored = Assert.Single(
            await reopened.GetActiveCaptureSessionsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(CaptureState.Saved, restored.State);
        Assert.Equal(saved.ItemId, restored.SavedItemId);
        Assert.True(restored.UndoDeadlineAt > DateTimeOffset.UtcNow);
        Assert.NotNull(await reopened.FindByHashAsync(restored.Hash!));
        Assert.Equal(
            1,
            (await reopened.SearchPageAsync(
                new SearchOptions(Query: "saved without AI", PageSize: 10))).TotalCount);
    }

    [Fact]
    public async Task DuplicatePreparedBeforePermanentDeletionCopiesNewManagedFiles()
    {
        var source = Path.Combine(_root, "stale.png");
        WriteSyntheticPng(source, 80, 60, 61);
        var coordinator = new CaptureCoordinator(_repository);
        var first = await coordinator.CreateFromFileAsync(source);
        var saved = await coordinator.SaveAsync(first, "first", "", null, []);
        using var duplicate = await coordinator.CreateFromFileAsync(source);
        Assert.NotNull(duplicate.ExistingItem);
        var oldPath = duplicate.ExistingItem.OriginalPath;
        await _repository.MoveToTrashAsync(saved.ItemId);
        await _repository.PermanentlyDeleteTrashItemsAsync([saved.ItemId]);

        var result = await coordinator.SaveAsync(duplicate, "second", "", null, []);

        Assert.False(result.WasDuplicate);
        var current = (await _repository.FindByHashAsync(duplicate.Hash))!;
        Assert.NotEqual(oldPath, current.OriginalPath);
        Assert.Empty((await _repository.InspectOrphanedFilesAsync()).MissingReferencedFiles);
        Assert.Equal(80, ImagePipeline.DecodeFirstFrame(_repository.Paths.ToAbsolute(current.OriginalPath)).PixelWidth);
        Assert.False(File.Exists(duplicate.StagedOriginal));
    }

    [Fact]
    public async Task DuplicateRepairsMissingFilesAndFailedSaveRetainsStaging()
    {
        var source = Path.Combine(_root, "repair.png");
        WriteSyntheticPng(source, 80, 60, 71);
        var coordinator = new CaptureCoordinator(_repository);
        var first = await coordinator.CreateFromFileAsync(source);
        await coordinator.SaveAsync(first, "first", "", null, []);
        using var pending = await coordinator.CreateFromFileAsync(source);
        var item = pending.ExistingItem!;
        File.Delete(_repository.Paths.ToAbsolute(item.OriginalPath));
        File.Delete(_repository.Paths.ToAbsolute(item.ThumbnailPath));

        await Assert.ThrowsAsync<SqliteException>(() => coordinator.SaveAsync(pending, "invalid", "", long.MaxValue, []));
        Assert.True(File.Exists(pending.StagedOriginal));
        Assert.Empty((await _repository.InspectOrphanedFilesAsync()).MissingReferencedFiles);
        Assert.Equal("first", (await _repository.FindByHashAsync(pending.Hash))!.Prompt);
        var result = await coordinator.SaveAsync(pending, "repaired", "", null, []);
        Assert.True(result.WasDuplicate);
        Assert.Equal(item.OriginalPath, (await _repository.FindByHashAsync(pending.Hash))!.OriginalPath);
        Assert.False(File.Exists(pending.StagedOriginal));
    }

    [Fact]
    public async Task DownloadClosesWriterBeforePreparingImage()
    {
        var source = Path.Combine(_root, "download.png");
        WriteSyntheticPng(source, 91, 73, 19);
        using var http = new System.Net.Http.HttpClient(new SyntheticHttpHandler(await File.ReadAllBytesAsync(source)));
        var coordinator = new CaptureCoordinator(_repository, http);
        using var pending = await coordinator.CreateFromUriAsync(new Uri("https://synthetic.invalid/image.png"));
        Assert.Equal(91, pending.Width);
        await coordinator.SaveAsync(pending, "downloaded", "", null, []);
        Assert.Empty((await _repository.InspectOrphanedFilesAsync()).MissingReferencedFiles);
    }

    [Fact]
    public async Task InvalidDownloadIsRecoverableAndCancellationCreatesNoSession()
    {
        using var http = new System.Net.Http.HttpClient(new SyntheticHttpHandler([1, 2, 3]));
        var coordinator = new CaptureCoordinator(_repository, http);
        await Assert.ThrowsAsync<CapturePreparationException>(() => coordinator.CreateFromUriAsync(new Uri("https://synthetic.invalid/invalid.png")));
        var failed = Assert.Single(await _repository.GetCaptureInboxAsync());
        Assert.True(File.Exists(_repository.Paths.ToAbsolute(failed.StagedOriginalPath!)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CreateFromUriAsync(new Uri("https://synthetic.invalid/image.png"), cancellation.Token));
        Assert.Single(await _repository.GetCaptureInboxAsync());
    }

    [Fact]
    public async Task CancellationAndConcurrentDuplicateSavePreserveStagingAndReferences()
    {
        var source = Path.Combine(_root, "concurrent.png");
        WriteSyntheticPng(source, 80, 60, 28);
        var coordinator = new CaptureCoordinator(_repository);
        using var first = await coordinator.CreateFromFileAsync(source);
        using var second = await coordinator.CreateFromFileAsync(source);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.SaveAsync(first, "canceled", "", null, [], canceled.Token));
        Assert.True(File.Exists(first.StagedOriginal));
        var saved = await Task.WhenAll(
            Task.Run(() => coordinator.SaveAsync(first, "first", "", null, [])),
            Task.Run(() => coordinator.SaveAsync(second, "second", "", null, [])));
        Assert.Equal(saved[0].ItemId, saved[1].ItemId);
        Assert.Single(saved, result => result.WasDuplicate);
        Assert.Empty((await _repository.InspectOrphanedFilesAsync()).MissingReferencedFiles);
        Assert.False(File.Exists(first.StagedOriginal));
        Assert.False(File.Exists(second.StagedOriginal));
    }

    [Fact]
    public async Task UndoPreservesManuallyChangedOriginalFile()
    {
        var source = Path.Combine(_root, "changed-original.png");
        WriteSyntheticPng(source, 80, 60, 21);
        var coordinator = new CaptureCoordinator(_repository);
        using var pending = await coordinator.CreateFromFileAsync(source);
        var saved = await coordinator.SaveAsync(pending, "original", "", null, []);
        var item = (await _repository.GetGalleryItemAsync(saved.ItemId))!;
        var managed = _repository.Paths.ToAbsolute(item.OriginalPath);
        await File.AppendAllTextAsync(managed, "manual change");
        var modified = await File.ReadAllBytesAsync(managed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.UndoCaptureAsync(pending.SessionId, DateTimeOffset.UtcNow));
        Assert.Equal(modified, await File.ReadAllBytesAsync(managed));
        Assert.NotNull(await _repository.GetGalleryItemAsync(saved.ItemId));
    }

    private static void WriteSyntheticPng(
        string path,
        int width,
        int height,
        byte seed)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(seed + index % 79);
            pixels[index + 1] = (byte)(seed + index % 113);
            pixels[index + 2] = (byte)(seed + index % 151);
            pixels[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
