using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    public async Task<long> BeginExternalFolderScanAsync(
        string folderId,
        string rootPath,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateExternalFolder(folderId, rootPath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var currentRoot = connection.CreateCommand();
        currentRoot.Transaction = transaction;
        currentRoot.CommandText = "SELECT root_path FROM external_folder_index WHERE folder_id = $folder;";
        currentRoot.Parameters.AddWithValue("$folder", folderId);
        var existingRoot = await currentRoot.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (existingRoot is not null
            && !string.Equals(existingRoot, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM external_file_index WHERE folder_id = $folder;";
            clear.Parameters.AddWithValue("$folder", folderId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var begin = connection.CreateCommand();
        begin.Transaction = transaction;
        begin.CommandText = """
            INSERT INTO external_folder_index(
                folder_id, root_path, status, last_scan_started_at, scan_generation)
            VALUES($folder, $root, $status, $started, 1)
            ON CONFLICT(folder_id) DO UPDATE SET
                root_path = excluded.root_path,
                status = excluded.status,
                last_scan_started_at = excluded.last_scan_started_at,
                last_error = NULL,
                scan_generation = external_folder_index.scan_generation + 1;
            SELECT scan_generation FROM external_folder_index WHERE folder_id = $folder;
            """;
        begin.Parameters.AddWithValue("$folder", folderId);
        begin.Parameters.AddWithValue("$root", rootPath);
        begin.Parameters.AddWithValue("$status", (int)ExternalFolderIndexStatus.Indexing);
        begin.Parameters.AddWithValue("$started", startedAt.ToUniversalTime().ToString("O"));
        var generation = Convert.ToInt64(
            await begin.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    public async Task UpsertExternalFilesAsync(
        string folderId,
        long generation,
        IReadOnlyList<ExternalFileIndexInput> files,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = CreateExternalFileUpsertCommand(connection, transaction);
        AddExternalFileParameters(command);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetExternalFileParameters(command, folderId, generation, file, indexedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task TouchExternalFilesAsync(
        string folderId,
        long generation,
        IReadOnlyList<string> normalizedPaths,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        if (normalizedPaths.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE external_file_index
            SET last_seen_generation = $generation,
                availability_status = $available,
                thumbnail_status = CASE
                    WHEN width > 0 AND height > 0 THEN $sourceReady
                    ELSE thumbnail_status
                END,
                last_error = NULL,
                indexed_at = $indexed
            WHERE folder_id = $folder AND normalized_path = $path;
            """;
        command.Parameters.Add("$generation", SqliteType.Integer);
        command.Parameters.Add("$available", SqliteType.Integer);
        command.Parameters.Add("$sourceReady", SqliteType.Integer);
        command.Parameters.Add("$indexed", SqliteType.Text);
        command.Parameters.Add("$folder", SqliteType.Text);
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters["$generation"].Value = generation;
        command.Parameters["$available"].Value = (int)ExternalFileAvailability.Available;
        command.Parameters["$sourceReady"].Value = (int)ExternalThumbnailStatus.SourceReady;
        command.Parameters["$indexed"].Value = indexedAt.ToUniversalTime().ToString("O");
        command.Parameters["$folder"].Value = folderId;
        foreach (var normalizedPath in normalizedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters["$path"].Value = normalizedPath;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteExternalFolderScanAsync(
        string folderId,
        long generation,
        DateTimeOffset completedAt,
        DateTimeOffset nextValidationAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var missing = connection.CreateCommand();
        missing.Transaction = transaction;
        missing.CommandText = """
            UPDATE external_file_index
            SET availability_status = $missing,
                last_error = $error,
                indexed_at = $completed
            WHERE folder_id = $folder
              AND last_seen_generation <> $generation;
            """;
        missing.Parameters.AddWithValue("$missing", (int)ExternalFileAvailability.Missing);
        missing.Parameters.AddWithValue("$error", "文件在最近一次完整校验中未找到。");
        missing.Parameters.AddWithValue("$completed", completedAt.ToUniversalTime().ToString("O"));
        missing.Parameters.AddWithValue("$folder", folderId);
        missing.Parameters.AddWithValue("$generation", generation);
        await missing.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = """
            UPDATE external_folder_index
            SET status = $status,
                last_scan_completed_at = $completed,
                next_validation_at = $next,
                last_error = NULL
            WHERE folder_id = $folder AND scan_generation = $generation;
            """;
        complete.Parameters.AddWithValue("$status", (int)ExternalFolderIndexStatus.Ready);
        complete.Parameters.AddWithValue("$completed", completedAt.ToUniversalTime().ToString("O"));
        complete.Parameters.AddWithValue("$next", nextValidationAt.ToUniversalTime().ToString("O"));
        complete.Parameters.AddWithValue("$folder", folderId);
        complete.Parameters.AddWithValue("$generation", generation);
        await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetExternalFolderIndexFailureAsync(
        string folderId,
        string rootPath,
        ExternalFolderIndexStatus status,
        string error,
        DateTimeOffset nextValidationAt,
        CancellationToken cancellationToken = default)
    {
        if (status is not (
            ExternalFolderIndexStatus.Missing
            or ExternalFolderIndexStatus.PermissionDenied
            or ExternalFolderIndexStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ValidateExternalFolder(folderId, rootPath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO external_folder_index(
                folder_id, root_path, status, next_validation_at, last_error)
            VALUES($folder, $root, $status, $next, $error)
            ON CONFLICT(folder_id) DO UPDATE SET
                root_path = excluded.root_path,
                status = excluded.status,
                next_validation_at = excluded.next_validation_at,
                last_error = excluded.last_error;
            """;
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$next", nextValidationAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$error", error);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, ExternalFileFingerprint>> GetExternalFileFingerprintsAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, normalized_path, file_size, modified_at, availability_status
            FROM external_file_index
            WHERE folder_id = $folder;
            """;
        command.Parameters.AddWithValue("$folder", folderId);
        var results = new Dictionary<string, ExternalFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = new ExternalFileFingerprint(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                (ExternalFileAvailability)reader.GetInt32(4));
            results[item.NormalizedPath] = item;
        }

        return results;
    }

    public async Task UpsertExternalFileAsync(
        string folderId,
        string rootPath,
        ExternalFileIndexInput file,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        var generation = await EnsureExternalFolderStateAsync(
            folderId,
            rootPath,
            cancellationToken).ConfigureAwait(false);
        await UpsertExternalFilesAsync(
            folderId,
            generation,
            [file],
            indexedAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameExternalFileAsync(
        string folderId,
        string rootPath,
        string oldNormalizedPath,
        ExternalFileIndexInput renamedFile,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        var generation = await EnsureExternalFolderStateAsync(
            folderId,
            rootPath,
            cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT id FROM external_file_index
            WHERE folder_id = $folder AND normalized_path = $oldPath;
            """;
        find.Parameters.AddWithValue("$folder", folderId);
        find.Parameters.AddWithValue("$oldPath", oldNormalizedPath);
        var existing = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var insert = CreateExternalFileUpsertCommand(connection, transaction);
            AddExternalFileParameters(insert);
            SetExternalFileParameters(insert, folderId, generation, renamedFile, indexedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var id = Convert.ToInt64(existing);
            var removeConflict = connection.CreateCommand();
            removeConflict.Transaction = transaction;
            removeConflict.CommandText = """
                DELETE FROM external_file_index
                WHERE folder_id = $folder
                  AND normalized_path = $newPath
                  AND id <> $id;
                """;
            removeConflict.Parameters.AddWithValue("$folder", folderId);
            removeConflict.Parameters.AddWithValue("$newPath", renamedFile.NormalizedPath);
            removeConflict.Parameters.AddWithValue("$id", id);
            await removeConflict.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE external_file_index
                SET path = $path,
                    normalized_path = $normalized,
                    file_name = $name,
                    file_size = $size,
                    modified_at = $modified,
                    width = $width,
                    height = $height,
                    format = $format,
                    thumbnail_status = $thumbnail,
                    availability_status = $availability,
                    last_error = $error,
                    last_seen_generation = $generation,
                    indexed_at = $indexed
                WHERE id = $id;
                """;
            AddExternalFileValueParameters(update);
            SetExternalFileValueParameters(update, renamedFile, generation, indexedAt);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkExternalFileUnavailableAsync(
        string folderId,
        string normalizedPath,
        ExternalFileAvailability availability,
        string error,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        if (availability == ExternalFileAvailability.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE external_file_index
            SET availability_status = $availability,
                last_error = $error,
                indexed_at = $indexed
            WHERE folder_id = $folder AND normalized_path = $path;
            """;
        command.Parameters.AddWithValue("$availability", (int)availability);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$indexed", indexedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$path", normalizedPath);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExternalFileSearchPage> SearchExternalFilesAsync(
        string folderId,
        string query,
        GallerySortOrder sort,
        int pageSize,
        ExternalFilePageCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            throw new ArgumentException("External folder id is required.", nameof(folderId));
        }

        var conditions = new List<string>
        {
            "folder_id = $folder",
            "availability_status = $available",
            $"""
            EXISTS(
                SELECT 1 FROM external_folder_index folder
                WHERE folder.folder_id = external_file_index.folder_id
                  AND folder.status IN (
                      {(int)ExternalFolderIndexStatus.Indexing},
                      {(int)ExternalFolderIndexStatus.Ready}))
            """
        };
        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > 0)
        {
            conditions.Add("file_name LIKE $query ESCAPE '\\' COLLATE NOCASE");
        }
        if (cursor is not null)
        {
            var comparison = sort == GallerySortOrder.OldestFirst ? ">" : "<";
            conditions.Add(
                $"(modified_at {comparison} $cursorModified OR (modified_at = $cursorModified AND id {comparison} $cursorId))");
        }

        var direction = sort == GallerySortOrder.OldestFirst ? "ASC" : "DESC";
        var limit = Math.Clamp(pageSize, 1, 1000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"""
            SELECT COUNT(*) FROM external_file_index
            WHERE folder_id = $folder
              AND availability_status = $available
              AND EXISTS(
                  SELECT 1 FROM external_folder_index folder
                  WHERE folder.folder_id = external_file_index.folder_id
                    AND folder.status IN (
                        {(int)ExternalFolderIndexStatus.Indexing},
                        {(int)ExternalFolderIndexStatus.Ready}))
              {(trimmedQuery.Length > 0 ? "AND file_name LIKE $query ESCAPE '\\' COLLATE NOCASE" : "")};
            """;
        AddExternalSearchParameters(count, folderId, trimmedQuery, null);
        var totalCount = Convert.ToInt64(
            await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);

        var page = connection.CreateCommand();
        page.Transaction = transaction;
        page.CommandText = $"""
            SELECT id, folder_id, path, file_name, file_size, modified_at,
                   width, height, format, thumbnail_status
            FROM external_file_index
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY modified_at {direction}, id {direction}
            LIMIT $limit;
            """;
        AddExternalSearchParameters(page, folderId, trimmedQuery, cursor);
        page.Parameters.AddWithValue("$limit", limit + 1);
        var results = new List<ExternalFileIndexItem>(limit + 1);
        await using (var reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new ExternalFileIndexItem(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    DateTimeOffset.Parse(reader.GetString(5)),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    (ExternalThumbnailStatus)reader.GetInt32(9)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ExternalFilePageCursor? nextCursor = null;
        if (results.Count > limit)
        {
            results.RemoveAt(results.Count - 1);
            var last = results[^1];
            nextCursor = new ExternalFilePageCursor(last.ModifiedAt, last.Id);
        }

        return new ExternalFileSearchPage(totalCount, results, nextCursor);
    }

    public async Task<ExternalFolderIndexState?> GetExternalFolderIndexStateAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExternalFolderIndexStateAsync(
            connection,
            folderId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, ExternalFolderIndexState>> GetExternalFolderIndexStatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = CreateExternalFolderStateCommand(connection);
        var results = new Dictionary<string, ExternalFolderIndexState>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = ReadExternalFolderIndexState(reader);
            results[state.FolderId] = state;
        }
        return results;
    }

    public async Task RemoveExternalFolderIndexAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM external_folder_index WHERE folder_id = $folder;";
        command.Parameters.AddWithValue("$folder", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> EnsureExternalFolderStateAsync(
        string folderId,
        string rootPath,
        CancellationToken cancellationToken)
    {
        ValidateExternalFolder(folderId, rootPath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO external_folder_index(folder_id, root_path, status, scan_generation)
            VALUES($folder, $root, $status, 1)
            ON CONFLICT(folder_id) DO UPDATE SET root_path = excluded.root_path;
            SELECT scan_generation FROM external_folder_index WHERE folder_id = $folder;
            """;
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$status", (int)ExternalFolderIndexStatus.Pending);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 1L);
    }

    private static SqliteCommand CreateExternalFileUpsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO external_file_index(
                folder_id, path, normalized_path, file_name, file_size, modified_at,
                width, height, format, thumbnail_status, availability_status,
                last_error, last_seen_generation, indexed_at)
            VALUES(
                $folder, $path, $normalized, $name, $size, $modified,
                $width, $height, $format, $thumbnail, $availability,
                $error, $generation, $indexed)
            ON CONFLICT(folder_id, normalized_path) DO UPDATE SET
                path = excluded.path,
                file_name = excluded.file_name,
                file_size = excluded.file_size,
                modified_at = excluded.modified_at,
                width = excluded.width,
                height = excluded.height,
                format = excluded.format,
                thumbnail_status = excluded.thumbnail_status,
                availability_status = excluded.availability_status,
                last_error = excluded.last_error,
                last_seen_generation = excluded.last_seen_generation,
                indexed_at = excluded.indexed_at;
            """;
        return command;
    }

    private static void AddExternalFileParameters(SqliteCommand command)
    {
        command.Parameters.Add("$folder", SqliteType.Text);
        AddExternalFileValueParameters(command);
    }

    private static void AddExternalFileValueParameters(SqliteCommand command)
    {
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$normalized", SqliteType.Text);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$size", SqliteType.Integer);
        command.Parameters.Add("$modified", SqliteType.Text);
        command.Parameters.Add("$width", SqliteType.Integer);
        command.Parameters.Add("$height", SqliteType.Integer);
        command.Parameters.Add("$format", SqliteType.Text);
        command.Parameters.Add("$thumbnail", SqliteType.Integer);
        command.Parameters.Add("$availability", SqliteType.Integer);
        command.Parameters.Add("$error", SqliteType.Text);
        command.Parameters.Add("$generation", SqliteType.Integer);
        command.Parameters.Add("$indexed", SqliteType.Text);
    }

    private static void SetExternalFileParameters(
        SqliteCommand command,
        string folderId,
        long generation,
        ExternalFileIndexInput file,
        DateTimeOffset indexedAt)
    {
        command.Parameters["$folder"].Value = folderId;
        SetExternalFileValueParameters(command, file, generation, indexedAt);
    }

    private static void SetExternalFileValueParameters(
        SqliteCommand command,
        ExternalFileIndexInput file,
        long generation,
        DateTimeOffset indexedAt)
    {
        command.Parameters["$path"].Value = file.Path;
        command.Parameters["$normalized"].Value = file.NormalizedPath;
        command.Parameters["$name"].Value = file.FileName;
        command.Parameters["$size"].Value = file.FileSize;
        command.Parameters["$modified"].Value = file.ModifiedAt.ToUniversalTime().ToString("O");
        command.Parameters["$width"].Value = Math.Max(0, file.Width);
        command.Parameters["$height"].Value = Math.Max(0, file.Height);
        command.Parameters["$format"].Value = file.Format;
        command.Parameters["$thumbnail"].Value = (int)file.ThumbnailStatus;
        command.Parameters["$availability"].Value = (int)file.Availability;
        command.Parameters["$error"].Value = (object?)file.Error ?? DBNull.Value;
        command.Parameters["$generation"].Value = generation;
        command.Parameters["$indexed"].Value = indexedAt.ToUniversalTime().ToString("O");
    }

    private static void AddExternalSearchParameters(
        SqliteCommand command,
        string folderId,
        string query,
        ExternalFilePageCursor? cursor)
    {
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$available", (int)ExternalFileAvailability.Available);
        if (query.Length > 0)
        {
            command.Parameters.AddWithValue("$query", $"%{EscapeLike(query)}%");
        }
        if (cursor is not null)
        {
            command.Parameters.AddWithValue(
                "$cursorModified",
                cursor.ModifiedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$cursorId", cursor.Id);
        }
    }

    private static SqliteCommand CreateExternalFolderStateCommand(
        SqliteConnection connection,
        string? folderId = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT f.folder_id, f.root_path, f.status,
                   SUM(CASE WHEN i.availability_status = {(int)ExternalFileAvailability.Available} THEN 1 ELSE 0 END),
                   SUM(CASE WHEN i.availability_status = {(int)ExternalFileAvailability.Missing} THEN 1 ELSE 0 END),
                   SUM(CASE WHEN i.availability_status IN (
                       {(int)ExternalFileAvailability.Unreadable},
                       {(int)ExternalFileAvailability.PermissionDenied}) THEN 1 ELSE 0 END),
                   f.last_scan_started_at, f.last_scan_completed_at,
                   f.next_validation_at, f.last_error, f.scan_generation
            FROM external_folder_index f
            LEFT JOIN external_file_index i ON i.folder_id = f.folder_id
            {(folderId is null ? "" : "WHERE f.folder_id = $folder")}
            GROUP BY f.folder_id, f.root_path, f.status, f.last_scan_started_at,
                     f.last_scan_completed_at, f.next_validation_at,
                     f.last_error, f.scan_generation
            ORDER BY f.folder_id;
            """;
        if (folderId is not null) command.Parameters.AddWithValue("$folder", folderId);
        return command;
    }

    private static async Task<ExternalFolderIndexState?> ReadExternalFolderIndexStateAsync(
        SqliteConnection connection,
        string folderId,
        CancellationToken cancellationToken)
    {
        var command = CreateExternalFolderStateCommand(connection, folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadExternalFolderIndexState(reader)
            : null;
    }

    private static ExternalFolderIndexState ReadExternalFolderIndexState(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        (ExternalFolderIndexStatus)reader.GetInt32(2),
        reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
        reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
        reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
        ReadOptionalDate(reader, 6),
        ReadOptionalDate(reader, 7),
        ReadOptionalDate(reader, 8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetInt64(10));

    private static DateTimeOffset? ReadOptionalDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static void ValidateExternalFolder(string folderId, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            throw new ArgumentException("External folder id is required.", nameof(folderId));
        }
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("External folder path is required.", nameof(rootPath));
        }
    }
}
