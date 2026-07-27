using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class LibraryUpgradeRecoveryTests
{
    public static TheoryData<LibraryUpgradePhase> FailurePhases => new()
    {
        LibraryUpgradePhase.Prepared,
        LibraryUpgradePhase.DatabaseBackupCreated,
        LibraryUpgradePhase.MigrationStepStarting,
        LibraryUpgradePhase.MigrationStepCompleted,
        LibraryUpgradePhase.MigrationCommitted,
        LibraryUpgradePhase.ValidationCompleted,
        LibraryUpgradePhase.Completed
    };

    [Theory]
    [MemberData(nameof(FailurePhases))]
    public async Task FailureAtEveryUpgradePhaseRestoresDatabaseSettingsAndPreservesImages(
        LibraryUpgradePhase failurePhase)
    {
        var fixture = await UpgradeFixture.CreateAsync();
        try
        {
            var injected = false;
            var addedDuringUpgrade = Path.Combine(fixture.Paths.Originals, $"during-{failurePhase}.png");
            var repository = new LibraryRepository(fixture.Paths);

            await Assert.ThrowsAnyAsync<Exception>(() => repository.InitializeAsync(
                new LibraryUpgradeOptions(
                    fixture.SettingsPath,
                    (checkpoint, _) =>
                    {
                        if (injected || checkpoint.Phase != failurePhase) return ValueTask.CompletedTask;
                        injected = true;
                        File.WriteAllText(fixture.SettingsPath, """{"LibraryRoot":"changed"}""");
                        File.WriteAllBytes(addedDuringUpgrade, [4, 5, 6]);
                        throw new InjectedUpgradeFailureException(failurePhase);
                    })));

            Assert.True(injected);
            Assert.Equal(1L, await ExecuteScalarAsync(
                fixture.Paths.Database,
                "SELECT version FROM schema_info;"));
            Assert.Equal(fixture.OriginalSettings, File.ReadAllText(fixture.SettingsPath));
            Assert.True(File.Exists(fixture.OriginalImagePath));
            Assert.True(File.Exists(addedDuringUpgrade));
            Assert.Equal(LibraryUpgradePhase.RolledBack, ReadPhase(fixture.Paths.UpgradeState));
            Assert.NotNull(repository.LastUpgradeRecovery);
            Assert.False(repository.LastUpgradeRecovery!.ResumedValidation);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task StartupResumesValidationWhenMigrationWasCommittedBeforeCrash()
    {
        var fixture = await UpgradeFixture.CreateAsync();
        try
        {
            string? committedState = null;
            var injected = false;
            var interrupted = new LibraryRepository(fixture.Paths);
            await Assert.ThrowsAnyAsync<Exception>(() => interrupted.InitializeAsync(
                new LibraryUpgradeOptions(
                    fixture.SettingsPath,
                    (checkpoint, _) =>
                    {
                        if (injected || checkpoint.Phase != LibraryUpgradePhase.MigrationCommitted)
                        {
                            return ValueTask.CompletedTask;
                        }
                        injected = true;
                        committedState = File.ReadAllText(fixture.Paths.UpgradeState);
                        throw new InjectedUpgradeFailureException(checkpoint.Phase);
                    })));

            var stateDirectory = Path.GetDirectoryName(fixture.Paths.UpgradeState)!;
            var failedDatabase = Directory.EnumerateFiles(
                    stateDirectory,
                    "failed-database-*.db",
                    SearchOption.AllDirectories)
                .Single();
            SqliteConnection.ClearAllPools();
            File.Copy(failedDatabase, fixture.Paths.Database, overwrite: true);
            File.WriteAllText(fixture.Paths.UpgradeState, committedState!);

            var resumed = new LibraryRepository(fixture.Paths);
            await resumed.InitializeAsync(new LibraryUpgradeOptions(fixture.SettingsPath));

            Assert.NotNull(resumed.LastUpgradeRecovery);
            Assert.True(resumed.LastUpgradeRecovery!.Recovered);
            Assert.True(resumed.LastUpgradeRecovery.ResumedValidation);
            Assert.Equal(12L, await ExecuteScalarAsync(
                fixture.Paths.Database,
                "SELECT version FROM schema_info;"));
            Assert.Equal(LibraryUpgradePhase.Completed, ReadPhase(fixture.Paths.UpgradeState));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task CompletedUpgradeWritesPreflightAndMetadataCoverageManifest()
    {
        var fixture = await UpgradeFixture.CreateAsync();
        try
        {
            var repository = new LibraryRepository(fixture.Paths);
            await repository.InitializeAsync(new LibraryUpgradeOptions(fixture.SettingsPath));

            var report = Assert.IsType<LibraryUpgradeReport>(repository.LastUpgrade);
            Assert.Equal("ok", report.Preflight.IntegrityResult);
            Assert.True(report.Preflight.AvailableBytes >= report.Preflight.RequiredBytes);
            Assert.True(File.Exists(report.DatabaseBackupPath));
            Assert.True(File.Exists(report.SettingsBackupPath));
            var manifestPath = Path.Combine(report.SessionDirectory, "backup-manifest.json");
            var manifest = File.ReadAllText(manifestPath);
            Assert.Contains("prompts, notes, categories, tags and favorites", manifest);
            Assert.Contains("Original images and thumbnails are never modified by rollback", manifest);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static LibraryUpgradePhase ReadPhase(string statePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        return (LibraryUpgradePhase)document.RootElement.GetProperty("Phase").GetInt32();
    }

    private static async Task<long> ExecuteScalarAsync(string databasePath, string sql)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class UpgradeFixture : IDisposable
    {
        private UpgradeFixture(
            string root,
            LibraryPaths paths,
            string settingsPath,
            string originalSettings,
            string originalImagePath)
        {
            Root = root;
            Paths = paths;
            SettingsPath = settingsPath;
            OriginalSettings = originalSettings;
            OriginalImagePath = originalImagePath;
        }

        public string Root { get; }
        public LibraryPaths Paths { get; }
        public string SettingsPath { get; }
        public string OriginalSettings { get; }
        public string OriginalImagePath { get; }

        public static async Task<UpgradeFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "PromptVaultUpgradeRecoveryTests",
                Guid.NewGuid().ToString("N"));
            var paths = new LibraryPaths(Path.Combine(root, "library"));
            var settingsPath = Path.Combine(root, "settings.json");
            const string originalSettings = """{"LibraryRoot":"synthetic","CaptureListeningEnabled":false}""";
            Directory.CreateDirectory(root);
            File.WriteAllText(settingsPath, originalSettings);

            var repository = new LibraryRepository(paths);
            await repository.InitializeAsync();
            var originalImagePath = Path.Combine(paths.Originals, "before.png");
            File.WriteAllBytes(originalImagePath, [1, 2, 3]);
            SqliteConnection.ClearAllPools();
            await ExecuteSqlAsync(paths.Database, """
                DROP INDEX IF EXISTS ix_items_category_created;
                DROP INDEX IF EXISTS ix_items_trash_created;
                DROP INDEX IF EXISTS ix_items_deleted;
                UPDATE schema_info SET version = 1;
                PRAGMA user_version = 1;
                """);
            return new UpgradeFixture(root, paths, settingsPath, originalSettings, originalImagePath);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }

        private static async Task ExecuteSqlAsync(string databasePath, string sql)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class InjectedUpgradeFailureException(LibraryUpgradePhase phase)
        : Exception($"Injected failure at {phase}.");
}
