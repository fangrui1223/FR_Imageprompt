using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed class LibraryRepository
{
    private readonly string _connectionString;
    private bool _ftsAvailable;

    public LibraryRepository(LibraryPaths paths)
    {
        EnsureSqliteProvider();
        Paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    private static int _providerInitialized;

    private static void EnsureSqliteProvider()
    {
        if (Interlocked.Exchange(ref _providerInitialized, 1) != 0) return;
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        SQLitePCL.raw.FreezeProvider();
    }

    public LibraryPaths Paths { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Paths.EnsureCreated();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;", cancellationToken);
        await ExecuteAsync(connection, SchemaSql, cancellationToken);
        _ftsAvailable = await TryInitializeFtsAsync(connection, cancellationToken).ConfigureAwait(false);
        await SeedCategoriesAsync(connection, cancellationToken).ConfigureAwait(false);
        await PurgeTrashAsync(30, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CategoryRecord>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, ai_description, sort_order FROM categories WHERE is_enabled = 1 ORDER BY sort_order, name;";
        var results = new List<CategoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CategoryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return results;
    }

    public async Task<long> AddCategoryAsync(string name, string description, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("\u5206\u7C7B\u540D\u79F0\u4E0D\u80FD\u4E3A\u7A7A\u3002", nameof(name));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO categories(name, ai_description, sort_order)
            VALUES($name, $description, COALESCE((SELECT MAX(sort_order) + 1 FROM categories), 0));
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", description.Trim());
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    public async Task DeleteCategoryAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var clear = connection.CreateCommand();
        clear.Transaction = (SqliteTransaction)transaction;
        clear.CommandText = "UPDATE collection_items SET category_id = NULL WHERE category_id = $id;";
        clear.Parameters.AddWithValue("$id", id);
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var delete = connection.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = "DELETE FROM categories WHERE id = $id;";
        delete.Parameters.AddWithValue("$id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }


    public async Task MoveCategoryAsync(long id, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var categories = new List<(long Id, int SortOrder, string Name)>();
        var list = connection.CreateCommand();
        list.Transaction = (SqliteTransaction)transaction;
        list.CommandText = "SELECT id, sort_order, name FROM categories WHERE is_enabled = 1 ORDER BY sort_order, name;";
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                categories.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2)));
            }
        }

        var index = categories.FindIndex(x => x.Id == id);
        var targetIndex = index + Math.Sign(direction);
        if (index < 0 || targetIndex < 0 || targetIndex >= categories.Count)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await SetCategorySortOrderAsync(connection, (SqliteTransaction)transaction, categories[index].Id, categories[targetIndex].SortOrder, cancellationToken).ConfigureAwait(false);
        await SetCategorySortOrderAsync(connection, (SqliteTransaction)transaction, categories[targetIndex].Id, categories[index].SortOrder, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetCategorySortOrderAsync(SqliteConnection connection, SqliteTransaction transaction, long id, int sortOrder, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE categories SET sort_order = $sort WHERE id = $id;";
        command.Parameters.AddWithValue("$sort", sortOrder);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GalleryItem?> FindByHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = BuildGalleryCommand(connection, "WHERE a.hash = $hash", "LIMIT 1");
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadGalleryItem(reader) : null;
    }

    public async Task<SaveResult> SaveAsync(SaveItemInput input, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT ci.id FROM image_assets a JOIN collection_items ci ON ci.asset_id = a.id WHERE a.hash = $hash LIMIT 1;";
        existing.Parameters.AddWithValue("$hash", input.Asset.Hash);
        var existingValue = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var wasDuplicate = existingValue is not null;
        long itemId;

        if (wasDuplicate)
        {
            itemId = Convert.ToInt64(existingValue);
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE collection_items
                SET prompt = $prompt, notes = $notes, category_id = $category, updated_at = $now, deleted_at = NULL
                WHERE id = $id;
                """;
            AddItemParameters(update, itemId, input);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var asset = connection.CreateCommand();
            asset.Transaction = transaction;
            asset.CommandText = """
                INSERT INTO image_assets(hash, original_path, thumbnail_path, medium_thumbnail_path, width, height, format, created_at)
                VALUES($hash, $original, $thumb, $medium, $width, $height, $format, $now);
                SELECT last_insert_rowid();
                """;
            asset.Parameters.AddWithValue("$hash", input.Asset.Hash);
            asset.Parameters.AddWithValue("$original", input.Asset.OriginalPath);
            asset.Parameters.AddWithValue("$thumb", input.Asset.ThumbnailPath);
            asset.Parameters.AddWithValue("$medium", input.Asset.MediumThumbnailPath);
            asset.Parameters.AddWithValue("$width", input.Asset.Width);
            asset.Parameters.AddWithValue("$height", input.Asset.Height);
            asset.Parameters.AddWithValue("$format", input.Asset.Format);
            asset.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            var assetId = Convert.ToInt64(await asset.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

            var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO collection_items(asset_id, prompt, notes, category_id, created_at, updated_at)
                VALUES($asset, $prompt, $notes, $category, $now, $now);
                SELECT last_insert_rowid();
                """;
            item.Parameters.AddWithValue("$asset", assetId);
            item.Parameters.AddWithValue("$prompt", input.Prompt.Trim());
            item.Parameters.AddWithValue("$notes", input.Notes.Trim());
            item.Parameters.AddWithValue("$category", (object?)input.CategoryId ?? DBNull.Value);
            item.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            itemId = Convert.ToInt64(await item.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        await ReplaceTagsAsync(connection, transaction, itemId, input.Tags, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SaveResult(itemId, wasDuplicate);
    }

    public async Task<IReadOnlyList<GalleryItem>> SearchAsync(SearchOptions options, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var conditions = new List<string> { options.IncludeTrash ? "ci.deleted_at IS NOT NULL" : "ci.deleted_at IS NULL" };
        if (options.CategoryId.HasValue) conditions.Add("ci.category_id = $category");
                var tagTerms = ParseTagText(options.Tag);
        for (var i = 0; i < tagTerms.Length; i++)
        {
            conditions.Add($"EXISTS(SELECT 1 FROM item_tags fit JOIN tags ft ON ft.id = fit.tag_id WHERE fit.item_id = ci.id AND ft.name LIKE $tag{i} ESCAPE '\\' COLLATE NOCASE)");
        }
        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            var useFts = ShouldUseFts(options.Query);
            conditions.Add(!useFts
                ? "(ci.prompt LIKE $like OR ci.notes LIKE $like)"
                : "ci.id IN (SELECT rowid FROM item_fts WHERE item_fts MATCH $query)");
        }

        var order = options.OldestFirst ? "ci.created_at ASC" : "ci.created_at DESC";
        var command = BuildGalleryCommand(connection, "WHERE " + string.Join(" AND ", conditions), $"ORDER BY {order} LIMIT $limit OFFSET $offset");
        if (options.CategoryId.HasValue) command.Parameters.AddWithValue("$category", options.CategoryId.Value);
        for (var i = 0; i < tagTerms.Length; i++) command.Parameters.AddWithValue($"$tag{i}", $"%{EscapeLike(tagTerms[i])}%");
        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            var query = options.Query.Trim();
            if (ShouldUseFts(query))
            {
                command.Parameters.AddWithValue("$query", $"\"{query.Replace("\"", "\"\"")}\"");
            }
            else
            {
                command.Parameters.AddWithValue("$like", $"%{query}%");
            }
        }
        command.Parameters.AddWithValue("$limit", Math.Clamp(options.Limit, 1, 5000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, options.Offset));

        var results = new List<GalleryItem>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadGalleryItem(reader));
        }
        catch (SqliteException) when (!string.IsNullOrWhiteSpace(options.Query) && ShouldUseFts(options.Query))
        {
            _ftsAvailable = false;
            return await SearchAsync(options, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }


    public async Task UpdateItemsMetadataAsync(IEnumerable<long> itemIds, string? tags, string? notes, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0 || (tags is null && notes is null)) return;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var parsedTags = tags is null ? null : ParseTagText(tags);
        foreach (var id in ids)
        {
            if (notes is not null)
            {
                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE collection_items SET notes = $notes, updated_at = $now WHERE id = $id;";
                update.Parameters.AddWithValue("$notes", notes.Trim());
                update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (parsedTags is not null)
            {
                await ReplaceTagsAsync(connection, transaction, id, parsedTags, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateItemsCategoryAsync(IEnumerable<long> itemIds, long? categoryId, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE collection_items SET category_id = $category, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PermanentlyDeleteTrashItemsAsync(IEnumerable<long> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0) return;

        var pathsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            long? assetId = null;
            var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT a.id, a.original_path, a.thumbnail_path, a.medium_thumbnail_path
                FROM collection_items ci
                JOIN image_assets a ON a.id = ci.asset_id
                WHERE ci.id = $id AND ci.deleted_at IS NOT NULL
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$id", id);
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) continue;
                assetId = reader.GetInt64(0);
                AddPath(reader.GetString(1));
                AddPath(reader.GetString(2));
                AddPath(reader.GetString(3));
            }

            var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM image_assets WHERE id = $assetId;";
            delete.Parameters.AddWithValue("$assetId", assetId.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var relativePath in pathsToDelete) TryDeleteLibraryFile(relativePath);

        void AddPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) pathsToDelete.Add(path);
        }
    }

    private void TryDeleteLibraryFile(string relativePath)
    {
        try
        {
            var path = Paths.ToAbsolute(relativePath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
    public async Task MoveToTrashAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE collection_items SET deleted_at = $now, updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE collection_items SET deleted_at = NULL, updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeTrashAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM collection_items WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SqliteCommand BuildGalleryCommand(SqliteConnection connection, string where, string tail)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ci.id, a.hash, a.original_path, a.thumbnail_path, a.width, a.height, a.format,
                   ci.prompt, ci.notes, ci.category_id, COALESCE(c.name, char(26410,20998,31867)),
                   COALESCE(GROUP_CONCAT(t.name, ', '), ''), ci.created_at, ci.deleted_at
            FROM collection_items ci
            JOIN image_assets a ON a.id = ci.asset_id
            LEFT JOIN categories c ON c.id = ci.category_id
            LEFT JOIN item_tags it ON it.item_id = ci.id
            LEFT JOIN tags t ON t.id = it.tag_id
            {where}
            GROUP BY ci.id
            {tail};
            """;
        return command;
    }

    private static GalleryItem ReadGalleryItem(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
        reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.GetString(10),
        reader.GetString(11), DateTimeOffset.Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)));

    private static void AddItemParameters(SqliteCommand command, long id, SaveItemInput input)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$prompt", input.Prompt.Trim());
        command.Parameters.AddWithValue("$notes", input.Notes.Trim());
        command.Parameters.AddWithValue("$category", (object?)input.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    }

    private static async Task ReplaceTagsAsync(SqliteConnection connection, SqliteTransaction transaction, long itemId, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM item_tags WHERE item_id = $id;";
        clear.Parameters.AddWithValue("$id", itemId);
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var tag in tags.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30))
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO tags(name) VALUES($name) ON CONFLICT(name) DO NOTHING;
                INSERT INTO item_tags(item_id, tag_id, source, confidence)
                SELECT $item, id, 'user', NULL FROM tags WHERE name = $name COLLATE NOCASE;
                """;
            insert.Parameters.AddWithValue("$name", tag);
            insert.Parameters.AddWithValue("$item", itemId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryInitializeFtsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(connection, FtsTrigramSql, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            try
            {
                await ExecuteAsync(connection, FtsUnicodeSql, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SqliteException)
            {
                return false;
            }
        }
    }

    private bool ShouldUseFts(string? query) => false;

    private static string[] ParseTagText(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split([',', '\uFF0C', ';', '\uFF1B', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\")
        .Replace("%", @"\%")
        .Replace("_", @"\_");
    private static async Task SeedCategoriesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO categories(name, ai_description, sort_order) VALUES
            (char(20154,29289), 'portrait, person, character, fashion', 10),
            (char(22330,26223), 'landscape, environment, nature, cityscape', 20),
            (char(20135,21697), 'product photography, object, commercial design', 30),
            (char(24314,31569), 'architecture, interior, building', 40),
            (char(25554,30011), 'illustration, anime, painting, concept art', 50),
            (char(30028,38754,35774,35745), 'user interface, application, web design', 60)
            ON CONFLICT(name) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);
        INSERT INTO schema_info(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_info);
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

    private const string FtsTrigramSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS item_fts USING fts5(prompt, notes, content='collection_items', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER IF NOT EXISTS item_fts_ai AFTER INSERT ON collection_items BEGIN
          INSERT INTO item_fts(rowid,prompt,notes) VALUES(new.id,new.prompt,new.notes); END;
        CREATE TRIGGER IF NOT EXISTS item_fts_ad AFTER DELETE ON collection_items BEGIN
          INSERT INTO item_fts(item_fts,rowid,prompt,notes) VALUES('delete',old.id,old.prompt,old.notes); END;
        CREATE TRIGGER IF NOT EXISTS item_fts_au AFTER UPDATE ON collection_items BEGIN
          INSERT INTO item_fts(item_fts,rowid,prompt,notes) VALUES('delete',old.id,old.prompt,old.notes);
          INSERT INTO item_fts(rowid,prompt,notes) VALUES(new.id,new.prompt,new.notes); END;
        INSERT INTO item_fts(item_fts) VALUES('rebuild');
        """;

    private const string FtsUnicodeSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS item_fts USING fts5(prompt, notes, content='collection_items', content_rowid='id', tokenize='unicode61');
        CREATE TRIGGER IF NOT EXISTS item_fts_ai AFTER INSERT ON collection_items BEGIN
          INSERT INTO item_fts(rowid,prompt,notes) VALUES(new.id,new.prompt,new.notes); END;
        CREATE TRIGGER IF NOT EXISTS item_fts_ad AFTER DELETE ON collection_items BEGIN
          INSERT INTO item_fts(item_fts,rowid,prompt,notes) VALUES('delete',old.id,old.prompt,old.notes); END;
        CREATE TRIGGER IF NOT EXISTS item_fts_au AFTER UPDATE ON collection_items BEGIN
          INSERT INTO item_fts(item_fts,rowid,prompt,notes) VALUES('delete',old.id,old.prompt,old.notes);
          INSERT INTO item_fts(rowid,prompt,notes) VALUES(new.id,new.prompt,new.notes); END;
        INSERT INTO item_fts(item_fts) VALUES('rebuild');
        """;
}
