using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed record DatabaseMigrationResult(int FromVersion, int ToVersion, string? BackupPath)
{
    public bool WasUpgraded => ToVersion > FromVersion;
}

public sealed class DatabaseMigrationException : Exception
{
    public DatabaseMigrationException(int fromVersion, int targetVersion, string? backupPath, Exception innerException)
        : base(CreateMessage(fromVersion, targetVersion, backupPath), innerException)
    {
        FromVersion = fromVersion;
        TargetVersion = targetVersion;
        BackupPath = backupPath;
    }

    public int FromVersion { get; }
    public int TargetVersion { get; }
    public string? BackupPath { get; }

    private static string CreateMessage(int fromVersion, int targetVersion, string? backupPath)
    {
        var recovery = string.IsNullOrWhiteSpace(backupPath)
            ? "原数据库未被升级，请检查磁盘空间和目录权限后重试。"
            : $"升级前备份已保留在“{backupPath}”，可用它恢复。";
        return $"图库数据库从版本 {fromVersion} 升级到版本 {targetVersion} 失败。{recovery}";
    }
}

internal static class DatabaseMigrations
{
    public const int LatestVersion = 7;

    private static readonly IReadOnlyList<Migration> Steps =
    [
        new(1, InitialSchemaSql),
        new(2, """
            CREATE INDEX IF NOT EXISTS ix_items_deleted ON collection_items(deleted_at);
            """),
        new(3, """
            CREATE INDEX IF NOT EXISTS ix_items_trash_created
                ON collection_items(deleted_at, created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_items_category_created
                ON collection_items(category_id, deleted_at, created_at DESC, id DESC);
            """),
        new(4, """
            CREATE TABLE IF NOT EXISTS search_index_state(
                id INTEGER PRIMARY KEY CHECK(id = 1),
                schema_version INTEGER NOT NULL DEFAULT 0,
                backend TEXT NOT NULL DEFAULT '',
                rebuilt_at TEXT NULL,
                rebuild_count INTEGER NOT NULL DEFAULT 0);
            INSERT INTO search_index_state(id, schema_version, backend, rebuilt_at, rebuild_count)
            VALUES(1, 0, '', NULL, 0)
            ON CONFLICT(id) DO NOTHING;
            """),
        new(5, """
            CREATE TABLE IF NOT EXISTS external_folder_index(
                folder_id TEXT PRIMARY KEY,
                root_path TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                last_scan_started_at TEXT NULL,
                last_scan_completed_at TEXT NULL,
                next_validation_at TEXT NULL,
                last_error TEXT NULL,
                scan_generation INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS external_file_index(
                id INTEGER PRIMARY KEY,
                folder_id TEXT NOT NULL REFERENCES external_folder_index(folder_id) ON DELETE CASCADE,
                path TEXT NOT NULL,
                normalized_path TEXT NOT NULL COLLATE NOCASE,
                file_name TEXT NOT NULL COLLATE NOCASE,
                file_size INTEGER NOT NULL,
                modified_at TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                format TEXT NOT NULL,
                thumbnail_status INTEGER NOT NULL DEFAULT 0,
                availability_status INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                last_seen_generation INTEGER NOT NULL DEFAULT 0,
                indexed_at TEXT NOT NULL,
                UNIQUE(folder_id, normalized_path));
            CREATE INDEX IF NOT EXISTS ix_external_file_folder_sort
                ON external_file_index(folder_id, availability_status, modified_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_external_file_folder_name
                ON external_file_index(folder_id, availability_status, file_name COLLATE NOCASE);
            """),
        new(6, """
            ALTER TABLE collection_items
                ADD COLUMN is_favorite INTEGER NOT NULL DEFAULT 0 CHECK(is_favorite IN (0, 1));
            CREATE INDEX IF NOT EXISTS ix_items_favorite_created
                ON collection_items(is_favorite, deleted_at, created_at DESC, id DESC);
            """),
        new(7, """
            CREATE TABLE IF NOT EXISTS capture_inbox(
                id TEXT PRIMARY KEY,
                state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 8),
                staged_original_path TEXT NOT NULL,
                staged_small_path TEXT NULL,
                staged_medium_path TEXT NULL,
                hash TEXT NULL,
                extension TEXT NOT NULL,
                format TEXT NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                prompt TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT '',
                category_id INTEGER NULL REFERENCES categories(id) ON DELETE SET NULL,
                tags TEXT NOT NULL DEFAULT '',
                captured_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                prompt_deadline_at TEXT NOT NULL,
                saved_item_id INTEGER NULL REFERENCES collection_items(id) ON DELETE SET NULL,
                was_duplicate INTEGER NOT NULL DEFAULT 0 CHECK(was_duplicate IN (0, 1)),
                undo_deadline_at TEXT NULL,
                previous_prompt TEXT NULL,
                previous_notes TEXT NULL,
                previous_category_id INTEGER NULL,
                previous_tags TEXT NULL,
                previous_deleted_at TEXT NULL,
                error TEXT NULL,
                last_clipboard_sequence INTEGER NULL,
                ai_summary TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_capture_inbox_state_updated
                ON capture_inbox(state, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_capture_inbox_prompt_deadline
                ON capture_inbox(state, prompt_deadline_at);
            """)
    ];

    public static async Task<DatabaseMigrationResult> ApplyAsync(
        SqliteConnection connection,
        LibraryPaths paths,
        bool databaseExisted,
        CancellationToken cancellationToken)
    {
        var fromVersion = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (fromVersion > LatestVersion)
        {
            throw new InvalidDataException(
                $"图库数据库版本 {fromVersion} 高于当前程序支持的版本 {LatestVersion}，请使用更新版本的 FR_Imageprompt。");
        }

        if (fromVersion == LatestVersion)
        {
            return new DatabaseMigrationResult(fromVersion, fromVersion, null);
        }

        string? backupPath = null;
        try
        {
            if (databaseExisted)
            {
                backupPath = CreateBackup(connection, paths, fromVersion);
            }

            foreach (var migration in Steps.Where(step => step.Version > fromVersion))
            {
                await ApplyStepAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            }

            return new DatabaseMigrationResult(fromVersion, LatestVersion, backupPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DatabaseMigrationException)
        {
            throw new DatabaseMigrationException(fromVersion, LatestVersion, backupPath, ex);
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='schema_info' LIMIT 1;";
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) return 0;

        var version = connection.CreateCommand();
        version.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_info;";
        return Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
    }

    private static string CreateBackup(SqliteConnection source, LibraryPaths paths, int fromVersion)
    {
        Directory.CreateDirectory(paths.DatabaseBackups);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff");
        var backupPath = Path.Combine(
            paths.DatabaseBackups,
            $"promptvault-v{fromVersion}-before-v{LatestVersion}-{timestamp}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var destination = new SqliteConnection(connectionString);
        destination.Open();
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static async Task ApplyStepAsync(
        SqliteConnection connection,
        Migration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Version == 6
                && await ColumnExistsAsync(
                    connection,
                    transaction,
                    "collection_items",
                    "is_favorite",
                    cancellationToken).ConfigureAwait(false)
                ? """
                    CREATE INDEX IF NOT EXISTS ix_items_favorite_created
                        ON collection_items(is_favorite, deleted_at, created_at DESC, id DESC);
                    """
                : migration.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var setVersion = connection.CreateCommand();
            setVersion.Transaction = transaction;
            setVersion.CommandText = $"""
                DELETE FROM schema_info;
                INSERT INTO schema_info(version) VALUES($version);
                PRAGMA user_version = {migration.Version};
                """;
            setVersion.Parameters.AddWithValue("$version", migration.Version);
            await setVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private sealed record Migration(int Version, string Sql);

    private const string InitialSchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS image_assets(
            id INTEGER PRIMARY KEY, hash TEXT NOT NULL UNIQUE, original_path TEXT NOT NULL,
            thumbnail_path TEXT NOT NULL, medium_thumbnail_path TEXT NOT NULL,
            width INTEGER NOT NULL, height INTEGER NOT NULL, format TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS categories(
            id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, ai_description TEXT NOT NULL DEFAULT '',
            sort_order INTEGER NOT NULL DEFAULT 0, is_enabled INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS collection_items(
            id INTEGER PRIMARY KEY, asset_id INTEGER NOT NULL UNIQUE REFERENCES image_assets(id) ON DELETE CASCADE,
            prompt TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', category_id INTEGER NULL REFERENCES categories(id) ON DELETE SET NULL,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL);
        CREATE TABLE IF NOT EXISTS tags(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS item_tags(
            item_id INTEGER NOT NULL REFERENCES collection_items(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            source TEXT NOT NULL DEFAULT 'user', confidence REAL NULL, PRIMARY KEY(item_id, tag_id));
        CREATE INDEX IF NOT EXISTS ix_items_category ON collection_items(category_id, deleted_at);
        CREATE INDEX IF NOT EXISTS ix_items_created ON collection_items(created_at DESC, deleted_at);
        CREATE INDEX IF NOT EXISTS ix_item_tags_tag ON item_tags(tag_id, item_id);
        """;
}
