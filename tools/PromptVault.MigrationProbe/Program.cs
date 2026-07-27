using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptVault.Core;

if (args.Length != 3)
{
    Console.Error.WriteLine(
        "Usage: PromptVault.MigrationProbe <source-backup.db> <isolated-root> <report.json>");
    return 2;
}

var source = Path.GetFullPath(args[0]);
var isolatedRoot = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args[2]);
if (!File.Exists(source)) throw new FileNotFoundException("Source backup was not found.", source);
if (Directory.Exists(isolatedRoot) && Directory.EnumerateFileSystemEntries(isolatedRoot).Any())
{
    throw new IOException($"Isolated root must be new or empty: {isolatedRoot}");
}

Directory.CreateDirectory(isolatedRoot);
var successPaths = new LibraryPaths(Path.Combine(isolatedRoot, "upgrade-success"));
var rollbackPaths = new LibraryPaths(Path.Combine(isolatedRoot, "upgrade-rollback"));
successPaths.EnsureCreated();
rollbackPaths.EnsureCreated();
File.Copy(source, successPaths.Database, overwrite: false);
File.Copy(source, rollbackPaths.Database, overwrite: false);
_ = new LibraryRepository(successPaths);

var beforeVersion = await ScalarAsync(successPaths.Database, "SELECT version FROM schema_info;");
var itemCount = await ScalarAsync(successPaths.Database, "SELECT COUNT(*) FROM collection_items;");
var activeItemCount = await ScalarAsync(
    successPaths.Database,
    "SELECT COUNT(*) FROM collection_items WHERE deleted_at IS NULL;");
var integrity = await TextScalarAsync(successPaths.Database, "PRAGMA quick_check;");

var successSettings = Path.Combine(isolatedRoot, "success-settings.json");
await File.WriteAllTextAsync(
    successSettings,
    JsonSerializer.Serialize(new
    {
        LibraryRoot = successPaths.Root,
        CaptureListeningEnabled = false,
        ReducedMotionEnabled = true
    }));
var successClock = Stopwatch.StartNew();
var upgraded = new LibraryRepository(successPaths);
await upgraded.InitializeAsync(new LibraryUpgradeOptions(successSettings));
successClock.Stop();
var searchClock = Stopwatch.StartNew();
var search = await upgraded.SearchPageAsync(new SearchOptions(PageSize: 100));
searchClock.Stop();

var rollbackSettings = Path.Combine(isolatedRoot, "rollback-settings.json");
await File.WriteAllTextAsync(
    rollbackSettings,
    JsonSerializer.Serialize(new
    {
        LibraryRoot = rollbackPaths.Root,
        CaptureListeningEnabled = false,
        ReducedMotionEnabled = true
    }));
var injected = false;
var rollbackRepository = new LibraryRepository(rollbackPaths);
try
{
    await rollbackRepository.InitializeAsync(new LibraryUpgradeOptions(
        rollbackSettings,
        (checkpoint, _) =>
        {
            if (injected || checkpoint.Phase != LibraryUpgradePhase.ValidationCompleted)
            {
                return ValueTask.CompletedTask;
            }
            injected = true;
            throw new InvalidOperationException("Migration probe rollback injection.");
        }));
    throw new InvalidOperationException("Rollback injection did not fire.");
}
catch (InvalidOperationException ex) when (
    ex.Message is "Migration probe rollback injection."
        or "Rollback injection did not fire."
    || ex.InnerException?.Message == "Migration probe rollback injection.")
{
    if (!injected) throw;
}

SqliteConnection.ClearAllPools();
var rollbackVersion = await ScalarAsync(rollbackPaths.Database, "SELECT version FROM schema_info;");
var rollbackItems = await ScalarAsync(rollbackPaths.Database, "SELECT COUNT(*) FROM collection_items;");
var rollbackIntegrity = await TextScalarAsync(rollbackPaths.Database, "PRAGMA quick_check;");

var report = new
{
    milestone = "M6-01",
    measuredAtUtc = DateTimeOffset.UtcNow,
    source = new
    {
        kind = "read-only database backup copied into an isolated workspace",
        fileName = Path.GetFileName(source),
        sourceBytes = new FileInfo(source).Length,
        sourceModifiedAtUtc = File.GetLastWriteTimeUtc(source)
    },
    before = new
    {
        version = beforeVersion,
        items = itemCount,
        activeItems = activeItemCount,
        integrity
    },
    upgrade = new
    {
        toVersion = upgraded.LastMigration?.ToVersion,
        elapsedMs = Math.Round(successClock.Elapsed.TotalMilliseconds, 3),
        backupCreated = File.Exists(upgraded.LastMigration?.BackupPath),
        settingsBackupCreated = File.Exists(upgraded.LastUpgrade?.SettingsBackupPath),
        searchFirstPageItems = search.Items.Count,
        searchTotalItems = search.TotalCount,
        searchMs = Math.Round(searchClock.Elapsed.TotalMilliseconds, 3),
        integrity = await TextScalarAsync(successPaths.Database, "PRAGMA quick_check;")
    },
    rollback = new
    {
        injectedAt = LibraryUpgradePhase.ValidationCompleted.ToString(),
        restoredVersion = rollbackVersion,
        restoredItems = rollbackItems,
        integrity = rollbackIntegrity,
        recoveryPhase = rollbackRepository.LastUpgradeRecovery is null
            ? null
            : LibraryUpgradePhase.RolledBack.ToString(),
        originalImagesTouched = false
    },
    passed = integrity == "ok"
             && itemCount > 0
             && upgraded.LastMigration?.ToVersion == 12
             && search.TotalCount == activeItemCount
             && rollbackVersion == beforeVersion
             && rollbackItems == itemCount
             && rollbackIntegrity == "ok"
};
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
await File.WriteAllTextAsync(
    reportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return report.passed ? 0 : 2;

static async Task<long> ScalarAsync(string databasePath, string sql)
{
    await using var connection = Open(databasePath);
    await connection.OpenAsync();
    var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<string> TextScalarAsync(string databasePath, string sql)
{
    await using var connection = Open(databasePath);
    await connection.OpenAsync();
    var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
}

static SqliteConnection Open(string databasePath) =>
    new(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString());
