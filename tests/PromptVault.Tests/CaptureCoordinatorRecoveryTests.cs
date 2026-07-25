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
