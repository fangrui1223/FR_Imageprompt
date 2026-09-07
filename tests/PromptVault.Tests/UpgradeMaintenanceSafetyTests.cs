using Microsoft.Data.Sqlite;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class UpgradeMaintenanceSafetyTests : IAsyncLifetime
{
    private readonly LibraryPaths _paths = new(Path.Combine(Path.GetTempPath(), "PromptVaultUpgradeSafety", Guid.NewGuid().ToString("N")));
    private long _itemId;
    private readonly string[] _files = ["originals/expired.png", "thumbnails/small/expired.jpg", "thumbnails/medium/expired.jpg"];

    public async Task InitializeAsync()
    {
        var repository = new LibraryRepository(_paths);
        await repository.InitializeAsync();
        foreach (var file in _files) await File.WriteAllBytesAsync(_paths.ToAbsolute(file), [1, 2, 3]);
        _itemId = (await repository.SaveAsync(new SaveItemInput(
            new AssetInput("expired", _files[0], _files[1], _files[2], 1, 1, "png"), "old", "", null, []))).ItemId;
        await repository.MoveToTrashAsync(_itemId);
        await SqlAsync("""
            UPDATE collection_items SET deleted_at='2000-01-01T00:00:00.0000000+00:00';
            DROP TABLE capture_undo_guards;
            UPDATE schema_info SET version=12;
            PRAGMA user_version=12;
            """);
        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData(LibraryUpgradePhase.ValidationCompleted)]
    [InlineData(LibraryUpgradePhase.Completed)]
    public async Task FailedUpgradeNeverDeletesFilesRestoredByRollback(LibraryUpgradePhase phase)
    {
        var repository = new LibraryRepository(_paths);
        await Assert.ThrowsAsync<IOException>(() => repository.InitializeAsync(new LibraryUpgradeOptions(Checkpoint: (step, _) =>
        {
            if (step.Phase == phase) throw new IOException("injected upgrade failure");
            return ValueTask.CompletedTask;
        })));
        Assert.True(repository.LastUpgradeRecovery!.Recovered);
        Assert.NotNull(await repository.FindByHashAsync("expired"));
        Assert.All(_files, file => Assert.True(File.Exists(_paths.ToAbsolute(file))));
        Assert.Empty((await repository.InspectOrphanedFilesAsync()).MissingReferencedFiles);
    }

    [Fact]
    public async Task CleanupFailureDoesNotRollBackCompletedUpgradeAndCanRetry()
    {
        await SqlAsync("CREATE TRIGGER fail_cleanup BEFORE DELETE ON collection_items BEGIN SELECT RAISE(ABORT, 'synthetic cleanup failure'); END;");
        var repository = new LibraryRepository(_paths);
        var diagnostics = new List<RepositoryDiagnostic>();
        repository.Diagnostic += (_, entry) => diagnostics.Add(entry);
        await repository.InitializeAsync();
        Assert.Equal(13, repository.LastMigration!.ToVersion);
        Assert.NotNull(repository.LastUpgrade);
        Assert.Contains(diagnostics, entry => entry.Area == "trash-maintenance");
        Assert.All(_files, file => Assert.True(File.Exists(_paths.ToAbsolute(file))));
        await SqlAsync("DROP TRIGGER fail_cleanup;");
        await new LibraryRepository(_paths).InitializeAsync();
        Assert.Null(await repository.FindByHashAsync("expired"));
        Assert.All(_files, file => Assert.False(File.Exists(_paths.ToAbsolute(file))));
    }

    private async Task SqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_paths.Database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_paths.Root, true);
        return Task.CompletedTask;
    }
}
