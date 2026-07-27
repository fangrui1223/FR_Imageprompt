using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public enum LibraryUpgradePhase
{
    Prepared,
    DatabaseBackupCreated,
    MigrationStepStarting,
    MigrationStepCompleted,
    MigrationCommitted,
    ValidationCompleted,
    Completed,
    Failed,
    RollbackStarting,
    RolledBack
}

public sealed record LibraryUpgradeCheckpoint(
    LibraryUpgradePhase Phase,
    int FromVersion,
    int TargetVersion,
    int? MigrationVersion,
    string SessionDirectory,
    string? DatabaseBackupPath);

public sealed record LibraryUpgradeOptions(
    string? SettingsFilePath = null,
    Func<LibraryUpgradeCheckpoint, CancellationToken, ValueTask>? Checkpoint = null);

public sealed record LibraryUpgradePreflight(
    int FromVersion,
    int TargetVersion,
    long DatabaseBytes,
    long AvailableBytes,
    long RequiredBytes,
    bool LibraryWritable,
    bool SettingsReadable,
    string IntegrityResult);

public sealed record LibraryUpgradeRecovery(
    bool Recovered,
    bool ResumedValidation,
    string Message,
    string? SessionDirectory,
    string? DatabaseBackupPath,
    int PreservedOriginalFileCount);

public sealed record LibraryUpgradeReport(
    LibraryUpgradePreflight Preflight,
    string SessionDirectory,
    string? DatabaseBackupPath,
    string? SettingsBackupPath,
    int PreservedOriginalFileCount,
    LibraryUpgradeRecovery? StartupRecovery);

internal sealed class LibraryUpgradeManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly LibraryPaths _paths;
    private readonly LibraryUpgradeOptions _options;
    private UpgradeState? _state;

    public LibraryUpgradeManager(LibraryPaths paths, LibraryUpgradeOptions? options)
    {
        _paths = paths;
        _options = options ?? new LibraryUpgradeOptions();
    }

    public async Task<LibraryUpgradeRecovery?> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || state.Phase is LibraryUpgradePhase.Completed or LibraryUpgradePhase.RolledBack)
        {
            return null;
        }

        if (state.Phase is LibraryUpgradePhase.MigrationCommitted or LibraryUpgradePhase.ValidationCompleted)
        {
            var validation = await ValidateDatabaseFileAsync(_paths.Database, cancellationToken).ConfigureAwait(false);
            if (validation.IsValid && validation.Version == state.TargetVersion)
            {
                state = state with
                {
                    Phase = LibraryUpgradePhase.Completed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    IntegrityResult = validation.IntegrityResult,
                    RecoveryMessage = "升级已提交；启动时已继续完成完整性验证。"
                };
                await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
                _state = state;
                return new LibraryUpgradeRecovery(
                    true,
                    true,
                    state.RecoveryMessage,
                    state.SessionDirectory,
                    state.DatabaseBackupPath,
                    CountOriginalFiles());
            }
        }

        _state = state;
        return await RollbackAsync(
            "检测到未完成的升级，已从升级前快照恢复数据库和设置。原图与缩略图未被覆盖。",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryUpgradePreflight?> PrepareAsync(
        SqliteConnection connection,
        int fromVersion,
        int targetVersion,
        CancellationToken cancellationToken)
    {
        if (fromVersion >= targetVersion) return null;

        var databaseBytes = File.Exists(_paths.Database) ? new FileInfo(_paths.Database).Length : 0;
        var requiredBytes = Math.Max(100L * 1024 * 1024, databaseBytes * 3 + 16L * 1024 * 1024);
        var availableBytes = ResolveAvailableBytes(_paths.Root);
        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"升级至少需要 {FormatBytes(requiredBytes)} 可用空间，当前仅有 {FormatBytes(availableBytes)}。");
        }

        var libraryWritable = ProbeWritableDirectory(_paths.UpgradeBackups);
        if (!libraryWritable)
        {
            throw new UnauthorizedAccessException($"图库升级备份目录不可写：{_paths.UpgradeBackups}");
        }

        var integrityResult = await ReadIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"升级前数据库完整性检查失败：{integrityResult}");
        }

        var settingsReadable = string.IsNullOrWhiteSpace(_options.SettingsFilePath)
            || !File.Exists(_options.SettingsFilePath)
            || CanReadFile(_options.SettingsFilePath);
        if (!settingsReadable)
        {
            throw new IOException($"设置文件无法读取：{_options.SettingsFilePath}");
        }

        var now = DateTimeOffset.UtcNow;
        var sessionDirectory = Path.Combine(
            _paths.UpgradeBackups,
            $"v{fromVersion}-to-v{targetVersion}-{now:yyyyMMdd-HHmmssfff}");
        Directory.CreateDirectory(sessionDirectory);

        string? settingsBackupPath = null;
        if (!string.IsNullOrWhiteSpace(_options.SettingsFilePath) && File.Exists(_options.SettingsFilePath))
        {
            settingsBackupPath = Path.Combine(sessionDirectory, "settings.json");
            File.Copy(_options.SettingsFilePath, settingsBackupPath, overwrite: false);
        }

        var preflight = new LibraryUpgradePreflight(
            fromVersion,
            targetVersion,
            databaseBytes,
            availableBytes,
            requiredBytes,
            libraryWritable,
            settingsReadable,
            integrityResult);
        _state = new UpgradeState(
            Guid.NewGuid().ToString("N"),
            LibraryUpgradePhase.Prepared,
            fromVersion,
            targetVersion,
            null,
            now,
            now,
            sessionDirectory,
            null,
            settingsBackupPath,
            _options.SettingsFilePath,
            CountOriginalFiles(),
            integrityResult,
            null);
        await WriteStateAsync(_state, cancellationToken).ConfigureAwait(false);
        await WriteManifestAsync(_state, preflight, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(null, cancellationToken).ConfigureAwait(false);
        return preflight;
    }

    public async ValueTask RecordMigrationCheckpointAsync(
        LibraryUpgradePhase phase,
        int? migrationVersion,
        string? databaseBackupPath,
        CancellationToken cancellationToken)
    {
        if (_state is null) return;
        _state = _state with
        {
            Phase = phase,
            MigrationVersion = migrationVersion,
            DatabaseBackupPath = databaseBackupPath ?? _state.DatabaseBackupPath,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await WriteStateAsync(_state, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(migrationVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryUpgradeReport?> CompleteAsync(
        LibraryUpgradePreflight? preflight,
        LibraryUpgradeRecovery? startupRecovery,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (_state is null || preflight is null) return null;
        var integrityResult = await ReadIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"升级后数据库完整性检查失败：{integrityResult}");
        }

        await RecordMigrationCheckpointAsync(
            LibraryUpgradePhase.ValidationCompleted,
            DatabaseMigrations.LatestVersion,
            _state.DatabaseBackupPath,
            cancellationToken).ConfigureAwait(false);
        _state = _state with
        {
            Phase = LibraryUpgradePhase.Completed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            IntegrityResult = integrityResult,
            RecoveryMessage = "升级、完整性验证和可恢复备份均已完成。"
        };
        await WriteStateAsync(_state, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(DatabaseMigrations.LatestVersion, cancellationToken).ConfigureAwait(false);
        return new LibraryUpgradeReport(
            preflight,
            _state.SessionDirectory,
            _state.DatabaseBackupPath,
            _state.SettingsBackupPath,
            CountOriginalFiles(),
            startupRecovery);
    }

    public async Task<LibraryUpgradeRecovery?> FailAndRollbackAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (_state is null) return null;
        _state = _state with
        {
            Phase = LibraryUpgradePhase.Failed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            RecoveryMessage = exception.Message
        };
        await WriteStateAsync(_state, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await NotifyAsync(_state.MigrationVersion, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The original upgrade failure remains the reason for rollback.
        }
        return await RollbackAsync(
            "升级未完成，已自动恢复数据库和设置。原图与缩略图未被覆盖。",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<LibraryUpgradeRecovery> RollbackAsync(string message, CancellationToken cancellationToken)
    {
        var state = _state ?? throw new InvalidOperationException("没有可回滚的升级状态。");
        state = state with
        {
            Phase = LibraryUpgradePhase.RollbackStarting,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            RecoveryMessage = message
        };
        _state = state;
        await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(state.MigrationVersion, cancellationToken).ConfigureAwait(false);

        SqliteConnection.ClearAllPools();
        if (!string.IsNullOrWhiteSpace(state.DatabaseBackupPath) && File.Exists(state.DatabaseBackupPath))
        {
            var failedCopy = Path.Combine(
                state.SessionDirectory,
                $"failed-database-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.db");
            if (File.Exists(_paths.Database))
            {
                File.Copy(_paths.Database, failedCopy, overwrite: false);
            }
            RestoreFileAtomically(state.DatabaseBackupPath, _paths.Database);
            TryDelete(_paths.Database + "-wal");
            TryDelete(_paths.Database + "-shm");
        }

        if (!string.IsNullOrWhiteSpace(state.SettingsBackupPath)
            && !string.IsNullOrWhiteSpace(state.SettingsFilePath)
            && File.Exists(state.SettingsBackupPath))
        {
            RestoreFileAtomically(state.SettingsBackupPath, state.SettingsFilePath);
        }

        state = state with
        {
            Phase = LibraryUpgradePhase.RolledBack,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            RecoveryMessage = message
        };
        _state = state;
        await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(state.MigrationVersion, cancellationToken).ConfigureAwait(false);
        return new LibraryUpgradeRecovery(
            true,
            false,
            message,
            state.SessionDirectory,
            state.DatabaseBackupPath,
            CountOriginalFiles());
    }

    private async ValueTask NotifyAsync(int? migrationVersion, CancellationToken cancellationToken)
    {
        if (_state is null || _options.Checkpoint is null) return;
        await _options.Checkpoint(
            new LibraryUpgradeCheckpoint(
                _state.Phase,
                _state.FromVersion,
                _state.TargetVersion,
                migrationVersion,
                _state.SessionDirectory,
                _state.DatabaseBackupPath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteStateAsync(UpgradeState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.UpgradeBackups);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await WriteTextAtomicallyAsync(_paths.UpgradeState, json, cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicallyAsync(
            Path.Combine(state.SessionDirectory, "upgrade-state.json"),
            json,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteManifestAsync(
        UpgradeState state,
        LibraryUpgradePreflight preflight,
        CancellationToken cancellationToken)
    {
        var manifest = new
        {
            state.OperationId,
            state.FromVersion,
            state.TargetVersion,
            state.StartedAtUtc,
            state.SettingsBackupPath,
            preflight,
            metadataCoverage = new[]
            {
                "SQLite database snapshot: prompts, notes, categories, tags and favorites",
                "SQLite database snapshot: inbox, AI review state, embeddings and board layouts",
                "Settings snapshot when a settings file was supplied",
                "Original images and thumbnails are never modified by rollback"
            }
        };
        await WriteTextAtomicallyAsync(
            Path.Combine(state.SessionDirectory, "backup-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpgradeState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.UpgradeState)) return null;
        try
        {
            await using var stream = File.OpenRead(_paths.UpgradeState);
            return await JsonSerializer.DeserializeAsync<UpgradeState>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"升级状态文件损坏：{_paths.UpgradeState}", ex);
        }
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<(bool IsValid, int Version, string IntegrityResult)> ValidateDatabaseFileAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) return (false, 0, "database missing");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var integrity = await ReadIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
        var version = await DatabaseMigrations.ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        return (string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase), version, integrity);
    }

    private static async Task<string> ReadIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            ?? "no result";
    }

    private int CountOriginalFiles() =>
        Directory.Exists(_paths.Originals)
            ? Directory.EnumerateFiles(_paths.Originals, "*", SearchOption.AllDirectories).Count()
            : 0;

    private static bool CanReadFile(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.CanRead;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return !File.Exists(probe);
        }
        catch
        {
            return false;
        }
    }

    private static long ResolveAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.00} GB"
            : $"{bytes / (1024d * 1024):0.00} MB";

    private static void RestoreFileAtomically(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".restore-" + Guid.NewGuid().ToString("N");
        File.Copy(source, temporary, overwrite: false);
        File.Move(temporary, target, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale SQLite sidecar is best-effort cleanup after all pools were cleared.
        }
    }

    private sealed record UpgradeState(
        string OperationId,
        LibraryUpgradePhase Phase,
        int FromVersion,
        int TargetVersion,
        int? MigrationVersion,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        string SessionDirectory,
        string? DatabaseBackupPath,
        string? SettingsBackupPath,
        string? SettingsFilePath,
        int OriginalFileCount,
        string IntegrityResult,
        string? RecoveryMessage);
}
