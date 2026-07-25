using Microsoft.Data.Sqlite;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class ExternalFolderIndexTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PromptVaultExternalIndexTests",
        Guid.NewGuid().ToString("N"));
    private LibraryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = new LibraryRepository(new LibraryPaths(Path.Combine(_root, "library")));
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
    public async Task FullScanCachesMetadataAndSkipsUnchangedFiles()
    {
        var folder = CreateFolder("cached");
        CreateImage(folder.Path, "one.jpg", 100);
        CreateImage(folder.Path, "two.png", 200);
        CreateImage(folder.Path, "three.webp", 300);
        File.WriteAllText(Path.Combine(folder.Path, "ignored.txt"), "not an image");
        var reader = new CountingMetadataReader();
        using var service = CreateService(reader);

        var first = await service.EnsureFolderIndexedAsync(folder, forceValidation: true);
        var firstPage = await _repository.SearchExternalFilesAsync(
            folder.Id,
            "",
            GallerySortOrder.NewestFirst,
            10);
        var second = await service.EnsureFolderIndexedAsync(folder, forceValidation: true);

        Assert.NotNull(first);
        Assert.Equal(ExternalFolderIndexStatus.Ready, first!.Status);
        Assert.Equal(3, first.AvailableFiles);
        Assert.Equal(3, reader.ReadCount);
        Assert.Equal(3, firstPage.TotalCount);
        Assert.All(firstPage.Items, item =>
        {
            Assert.True(item.Width > 0);
            Assert.True(item.Height > 0);
            Assert.Equal(ExternalThumbnailStatus.SourceReady, item.ThumbnailStatus);
        });
        Assert.NotNull(second);
        Assert.Equal(3, reader.ReadCount);
        Assert.True(second!.ScanGeneration > first.ScanGeneration);

        var changedPath = Path.Combine(folder.Path, "two.png");
        File.AppendAllText(changedPath, "changed");
        File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(2));
        await service.EnsureFolderIndexedAsync(folder, forceValidation: true);

        Assert.Equal(4, reader.ReadCount);
    }

    [Fact]
    public async Task IncrementalRenamePreservesStableIdAndDeleteBecomesExplicitMissingState()
    {
        var folder = CreateFolder("changes");
        var oldPath = CreateImage(folder.Path, "before.jpg", 100);
        var reader = new CountingMetadataReader();
        using var service = CreateService(reader);
        await service.EnsureFolderIndexedAsync(folder, forceValidation: true);
        var before = Assert.Single((await _repository.SearchExternalFilesAsync(
            folder.Id,
            "",
            GallerySortOrder.NewestFirst,
            10)).Items);

        var renamedPath = Path.Combine(folder.Path, "after.jpg");
        File.Move(oldPath, renamedPath);
        await service.ProcessFileChangeAsync(
            folder.Id,
            ExternalFolderIndexService.ExternalFileChangeKind.Renamed,
            renamedPath,
            oldPath);
        var renamed = Assert.Single((await _repository.SearchExternalFilesAsync(
            folder.Id,
            "after",
            GallerySortOrder.NewestFirst,
            10)).Items);

        Assert.Equal(before.Id, renamed.Id);
        Assert.Equal("after.jpg", renamed.FileName);

        File.Delete(renamedPath);
        await service.ProcessFileChangeAsync(
            folder.Id,
            ExternalFolderIndexService.ExternalFileChangeKind.Deleted,
            renamedPath);

        var afterDelete = await _repository.SearchExternalFilesAsync(
            folder.Id,
            "",
            GallerySortOrder.NewestFirst,
            10);
        var state = await service.GetStateAsync(folder.Id);
        Assert.Equal(0, afterDelete.TotalCount);
        Assert.NotNull(state);
        Assert.Equal(1, state!.MissingFiles);
    }

    [Fact]
    public async Task UnreadableFilesAreReportedButExcludedFromGallery()
    {
        var folder = CreateFolder("unreadable");
        CreateImage(folder.Path, "good.jpg", 100);
        CreateImage(folder.Path, "broken.jpg", 200);
        var reader = new CountingMetadataReader(path =>
            path.EndsWith("broken.jpg", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidDataException("broken header")
                : new ExternalImageMetadata(800, 600, "jpg"));
        using var service = CreateService(reader);

        var state = await service.EnsureFolderIndexedAsync(folder, forceValidation: true);
        var page = await _repository.SearchExternalFilesAsync(
            folder.Id,
            "",
            GallerySortOrder.NewestFirst,
            10);

        Assert.NotNull(state);
        Assert.Equal(ExternalFolderIndexStatus.Ready, state!.Status);
        Assert.Equal(1, state.AvailableFiles);
        Assert.Equal(1, state.FailedFiles);
        Assert.Single(page.Items);
        Assert.Equal("good.jpg", page.Items[0].FileName);
    }

    [Fact]
    public async Task MissingFolderHasExplicitStateAndDoesNotExposeStaleRows()
    {
        var folder = CreateFolder("missing");
        CreateImage(folder.Path, "visible.jpg", 100);
        using var service = CreateService(new CountingMetadataReader());
        await service.EnsureFolderIndexedAsync(folder, forceValidation: true);
        Directory.Delete(folder.Path, true);

        var state = await service.EnsureFolderIndexedAsync(folder, forceValidation: true);
        var page = await _repository.SearchExternalFilesAsync(
            folder.Id,
            "",
            GallerySortOrder.NewestFirst,
            10);

        Assert.NotNull(state);
        Assert.Equal(ExternalFolderIndexStatus.Missing, state!.Status);
        Assert.Contains("不可用", state.LastError);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ExternalCursorPagesHaveNoDuplicatesOrOmissions()
    {
        var folder = CreateFolder("cursor");
        var modified = DateTime.UtcNow.AddMinutes(-10);
        for (var index = 0; index < 17; index++)
        {
            var path = CreateImage(folder.Path, $"image-{index:D2}.jpg", 100 + index);
            File.SetLastWriteTimeUtc(path, modified);
        }
        using var service = CreateService(new CountingMetadataReader());
        await service.EnsureFolderIndexedAsync(folder, forceValidation: true);

        var seen = new List<long>();
        ExternalFilePageCursor? cursor = null;
        do
        {
            var page = await _repository.SearchExternalFilesAsync(
                folder.Id,
                "",
                GallerySortOrder.NewestFirst,
                4,
                cursor);
            Assert.Equal(17, page.TotalCount);
            seen.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(17, seen.Count);
        Assert.Equal(17, seen.Distinct().Count());
    }

    [Fact]
    public async Task RemovingIndexNeverDeletesExternalFiles()
    {
        var folder = CreateFolder("remove");
        var image = CreateImage(folder.Path, "keep.jpg", 100);
        using var service = CreateService(new CountingMetadataReader());
        await service.EnsureFolderIndexedAsync(folder, forceValidation: true);

        await service.RemoveFolderAsync(folder.Id);

        Assert.True(File.Exists(image));
        Assert.Null(await service.GetStateAsync(folder.Id));
        Assert.Equal(0, (await _repository.SearchExternalFilesAsync(
            folder.Id,
            "",
            GallerySortOrder.NewestFirst,
            10)).TotalCount);
    }

    [Fact]
    public async Task RemovingFolderCancelsInFlightScanBeforeDeletingIndex()
    {
        var folder = CreateFolder("remove-during-scan");
        for (var index = 0; index < 20; index++)
        {
            CreateImage(folder.Path, $"image-{index:D2}.jpg", 100 + index);
        }
        var reader = new BlockingMetadataReader();
        using var service = CreateService(reader);
        var scan = service.EnsureFolderIndexedAsync(folder, forceValidation: true);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var removal = service.RemoveFolderAsync(folder.Id);
        reader.Release.Set();
        await removal;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan);
        Assert.Null(await service.GetStateAsync(folder.Id));
        Assert.Equal(0, (await _repository.SearchExternalFilesAsync(
            folder.Id,
            "",
            GallerySortOrder.NewestFirst,
            10)).TotalCount);
    }

    [Fact]
    public async Task VersionFourDatabaseIsBackedUpBeforeAddingExternalIndex()
    {
        SqliteConnection.ClearAllPools();
        await ExecuteSqlAsync(_repository.Paths.Database, """
            DROP INDEX IF EXISTS ix_external_file_folder_sort;
            DROP INDEX IF EXISTS ix_external_file_folder_name;
            DROP TABLE IF EXISTS external_file_index;
            DROP TABLE IF EXISTS external_folder_index;
            UPDATE schema_info SET version = 4;
            PRAGMA user_version = 4;
            """);

        var upgraded = new LibraryRepository(_repository.Paths);
        await upgraded.InitializeAsync();

        Assert.NotNull(upgraded.LastMigration);
        Assert.Equal(4, upgraded.LastMigration!.FromVersion);
        Assert.Equal(6, upgraded.LastMigration.ToVersion);
        Assert.True(File.Exists(upgraded.LastMigration.BackupPath));
        Assert.Equal(4L, await ExecuteScalarAsync(
            upgraded.LastMigration.BackupPath!,
            "SELECT version FROM schema_info;"));
        Assert.Equal(2L, await ExecuteScalarAsync(
            upgraded.Paths.Database,
            """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('external_folder_index', 'external_file_index');
            """));
    }

    private ExternalFolderIndexService CreateService(IExternalImageMetadataReader reader) => new(
        _repository,
        reader,
        validationInterval: TimeSpan.FromHours(6),
        watcherDebounce: TimeSpan.FromMilliseconds(20));

    private ExternalFolderSetting CreateFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return new ExternalFolderSetting
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Path = path,
            AddedAt = DateTimeOffset.UtcNow
        };
    }

    private static string CreateImage(string directory, string name, int length)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Enumerable.Range(0, length).Select(value => (byte)value).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-length));
        return path;
    }

    private static async Task ExecuteSqlAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class CountingMetadataReader(
        Func<string, ExternalImageMetadata>? read = null) : IExternalImageMetadataReader
    {
        private readonly Func<string, ExternalImageMetadata> _read =
            read ?? (_ => new ExternalImageMetadata(800, 600, "jpg"));
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ExternalImageMetadata Read(string path)
        {
            Interlocked.Increment(ref _readCount);
            return _read(path);
        }
    }

    private sealed class BlockingMetadataReader : IExternalImageMetadataReader
    {
        private int _started;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public ExternalImageMetadata Read(string path)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) Started.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(5));
            return new ExternalImageMetadata(800, 600, "jpg");
        }
    }
}
