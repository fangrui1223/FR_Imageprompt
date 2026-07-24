using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    private readonly string _connectionString;
    private SearchIndexBackend _searchIndexBackend;

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
    public DatabaseMigrationResult? LastMigration { get; private set; }
    public bool FullTextSearchAvailable => _searchIndexBackend == SearchIndexBackend.Trigram;
    public event EventHandler<RepositoryDiagnostic>? Diagnostic;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Paths.EnsureCreated();
        var databaseExisted = File.Exists(Paths.Database) && new FileInfo(Paths.Database).Length > 0;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;", cancellationToken);
        LastMigration = await DatabaseMigrations.ApplyAsync(
            connection,
            Paths,
            databaseExisted,
            cancellationToken).ConfigureAwait(false);
        _searchIndexBackend = await InitializeSearchIndexAsync(connection, cancellationToken).ConfigureAwait(false);
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

    public async Task<GallerySearchPage> SearchPageAsync(
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Source != GallerySourceKind.Library)
        {
            throw new ArgumentException(
                "LibraryRepository can only query the managed library source.",
                nameof(options));
        }

        if (options.CategoryId.HasValue && options.UncategorizedOnly)
        {
            throw new ArgumentException(
                "A category and the unclassified-only filter cannot be used together.",
                nameof(options));
        }

        var searchPlan = CreateSearchPlan(options.Query);
        try
        {
            return await SearchPageCoreAsync(options, searchPlan, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (
            searchPlan.Mode == SearchTextMode.Trigram
            && IsSearchIndexFailure(ex))
        {
            _searchIndexBackend = SearchIndexBackend.None;
            Trace.TraceWarning($"PromptVault full-text search failed and switched to LIKE fallback: {ex}");
            Diagnostic?.Invoke(this, new RepositoryDiagnostic(
                "search-index",
                "全文搜索索引暂不可用，已自动切换到兼容搜索；结果不会丢失，但大图库搜索可能稍慢。",
                ex));
            return await SearchPageCoreAsync(
                options,
                CreateSearchPlan(options.Query),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GallerySearchPage> SearchPageCoreAsync(
        SearchOptions options,
        SearchPlan searchPlan,
        CancellationToken cancellationToken)
    {
        var tagTerms = ParseTagText(options.Tag);
        var baseConditions = BuildSearchConditions(options, tagTerms, searchPlan, includeCursor: false);
        var pageConditions = BuildSearchConditions(options, tagTerms, searchPlan, includeCursor: true);
        var pageSize = Math.Clamp(options.PageSize, 1, 1000);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"""
            SELECT COUNT(*)
            FROM collection_items ci
            JOIN image_assets a ON a.id = ci.asset_id
            WHERE {string.Join(" AND ", baseConditions)};
            """;
        AddSearchParameters(count, options, tagTerms, searchPlan, includeCursor: false);
        var totalCount = Convert.ToInt64(
            await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);

        var direction = options.Sort == GallerySortOrder.OldestFirst ? "ASC" : "DESC";
        var command = BuildGalleryCommand(
            connection,
            $"WHERE {string.Join(" AND ", pageConditions)}",
            $"ORDER BY ci.created_at {direction}, ci.id {direction} LIMIT $pageLimit",
            transaction);
        AddSearchParameters(command, options, tagTerms, searchPlan, includeCursor: true);
        command.Parameters.AddWithValue("$pageLimit", pageSize + 1);

        var results = new List<GalleryItem>(pageSize + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(ReadGalleryItem(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        GalleryPageCursor? nextCursor = null;
        if (results.Count > pageSize)
        {
            results.RemoveAt(results.Count - 1);
            var last = results[^1];
            nextCursor = new GalleryPageCursor(last.CreatedAt, last.Id);
        }

        return new GallerySearchPage(totalCount, results, nextCursor);
    }

    private static List<string> BuildSearchConditions(
        SearchOptions options,
        IReadOnlyList<string> tagTerms,
        SearchPlan searchPlan,
        bool includeCursor)
    {
        var conditions = new List<string>
        {
            options.Trash == GalleryTrashScope.Trash
                ? "ci.deleted_at IS NOT NULL"
                : "ci.deleted_at IS NULL"
        };

        if (options.CategoryId.HasValue)
        {
            conditions.Add("ci.category_id = $category");
        }
        else if (options.UncategorizedOnly)
        {
            conditions.Add("ci.category_id IS NULL");
        }

        for (var i = 0; i < tagTerms.Count; i++)
        {
            conditions.Add($"EXISTS(SELECT 1 FROM item_tags fit JOIN tags ft ON ft.id = fit.tag_id WHERE fit.item_id = ci.id AND ft.name LIKE $tag{i} ESCAPE '\\' COLLATE NOCASE)");
        }

        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            conditions.Add(searchPlan.Mode != SearchTextMode.Trigram
                ? "(ci.prompt LIKE $like ESCAPE '\\' OR ci.notes LIKE $like ESCAPE '\\')"
                : "ci.id IN (SELECT rowid FROM item_fts WHERE item_fts MATCH $query)");
        }

        if (includeCursor && options.Cursor is not null)
        {
            var comparison = options.Sort == GallerySortOrder.OldestFirst ? ">" : "<";
            conditions.Add(
                $"(ci.created_at {comparison} $cursorCreated OR (ci.created_at = $cursorCreated AND ci.id {comparison} $cursorId))");
        }

        return conditions;
    }

    private static void AddSearchParameters(
        SqliteCommand command,
        SearchOptions options,
        IReadOnlyList<string> tagTerms,
        SearchPlan searchPlan,
        bool includeCursor)
    {
        if (options.CategoryId.HasValue)
        {
            command.Parameters.AddWithValue("$category", options.CategoryId.Value);
        }

        for (var i = 0; i < tagTerms.Count; i++)
        {
            command.Parameters.AddWithValue($"$tag{i}", $"%{EscapeLike(tagTerms[i])}%");
        }

        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            if (searchPlan.Mode == SearchTextMode.Trigram)
            {
                command.Parameters.AddWithValue("$query", searchPlan.Parameter);
            }
            else
            {
                command.Parameters.AddWithValue("$like", $"%{EscapeLike(searchPlan.Parameter)}%");
            }
        }

        if (includeCursor && options.Cursor is not null)
        {
            command.Parameters.AddWithValue("$cursorCreated", options.Cursor.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$cursorId", options.Cursor.Id);
        }
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

    public async Task<TrashPurgeResult> PermanentlyDeleteTrashItemsAsync(
        IEnumerable<long> itemIds,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0) return new TrashPurgeResult(0, 0, []);

        var pathsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedAssets = 0;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            long? assetId = null;
            string? originalPath = null;
            string? smallPath = null;
            string? mediumPath = null;
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
                originalPath = reader.GetString(1);
                smallPath = reader.GetString(2);
                mediumPath = reader.GetString(3);
            }

            var deleteItem = connection.CreateCommand();
            deleteItem.Transaction = transaction;
            deleteItem.CommandText = "DELETE FROM collection_items WHERE id = $id AND deleted_at IS NOT NULL;";
            deleteItem.Parameters.AddWithValue("$id", id);
            if (await deleteItem.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) continue;

            var deleteAsset = connection.CreateCommand();
            deleteAsset.Transaction = transaction;
            deleteAsset.CommandText = """
                DELETE FROM image_assets
                WHERE id = $assetId
                  AND NOT EXISTS(SELECT 1 FROM collection_items WHERE asset_id = $assetId);
                """;
            deleteAsset.Parameters.AddWithValue("$assetId", assetId.Value);
            if (await deleteAsset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                deletedAssets++;
                AddPath(originalPath);
                AddPath(smallPath);
                AddPath(mediumPath);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var fileResult = DeleteLibraryFiles(pathsToDelete);
        return new TrashPurgeResult(deletedAssets, fileResult.DeletedFiles, fileResult.Failures);

        void AddPath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) pathsToDelete.Add(path);
        }
    }

    private (int DeletedFiles, IReadOnlyList<FileDeletionFailure> Failures) DeleteLibraryFiles(
        IEnumerable<string> relativePaths)
    {
        var deletedFiles = 0;
        var failures = new List<FileDeletionFailure>();
        foreach (var relativePath in relativePaths)
        {
            try
            {
                var path = Paths.ToAbsolute(relativePath);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deletedFiles++;
            }
            catch (Exception ex)
            {
                failures.Add(new FileDeletionFailure(relativePath, ex.Message));
                Trace.TraceWarning($"PromptVault file cleanup failed for '{relativePath}': {ex}");
                Diagnostic?.Invoke(this, new RepositoryDiagnostic(
                    "file-cleanup",
                    $"图库文件清理失败：{relativePath}",
                    ex));
            }
        }

        return (deletedFiles, failures);
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

    public async Task<TrashPurgeResult> PurgeTrashAsync(
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays < 0) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");
        var assets = new List<(long Id, string Original, string Small, string Medium)>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT a.id, a.original_path, a.thumbnail_path, a.medium_thumbnail_path
            FROM image_assets a
            WHERE EXISTS(
                SELECT 1
                FROM collection_items expired
                WHERE expired.asset_id = a.id
                  AND expired.deleted_at IS NOT NULL
                  AND expired.deleted_at < $cutoff)
              AND NOT EXISTS(
                SELECT 1
                FROM collection_items retained
                WHERE retained.asset_id = a.id
                  AND (retained.deleted_at IS NULL OR retained.deleted_at >= $cutoff));
            """;
        select.Parameters.AddWithValue("$cutoff", cutoff);
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                assets.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM image_assets WHERE id = $id;";
        var idParameter = delete.Parameters.Add("$id", SqliteType.Integer);
        var deletedAssets = 0;
        foreach (var asset in assets)
        {
            idParameter.Value = asset.Id;
            deletedAssets += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var paths = assets.SelectMany(asset => new[] { asset.Original, asset.Small, asset.Medium });
        var fileResult = DeleteLibraryFiles(paths);
        return new TrashPurgeResult(deletedAssets, fileResult.DeletedFiles, fileResult.Failures);
    }

    public async Task<OrphanedFileReport> InspectOrphanedFilesAsync(
        CancellationToken cancellationToken = default)
    {
        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingFiles = new List<string>();
        var assetsWithoutItems = new List<long>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var assets = connection.CreateCommand();
        assets.CommandText = """
            SELECT a.id, a.original_path, a.thumbnail_path, a.medium_thumbnail_path,
                   EXISTS(SELECT 1 FROM collection_items ci WHERE ci.asset_id = a.id)
            FROM image_assets a;
            """;
        await using (var reader = await assets.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assetId = reader.GetInt64(0);
                if (!reader.GetBoolean(4)) assetsWithoutItems.Add(assetId);
                for (var column = 1; column <= 3; column++)
                {
                    var relativePath = reader.GetString(column);
                    try
                    {
                        var absolutePath = Paths.ToAbsolute(relativePath);
                        referencedFiles.Add(absolutePath);
                        if (!File.Exists(absolutePath)) missingFiles.Add(relativePath);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
                    {
                        missingFiles.Add(relativePath);
                    }
                }
            }
        }

        var unreferencedFiles = new List<string>();
        foreach (var directory in new[] { Paths.Originals, Paths.SmallThumbnails, Paths.MediumThumbnails })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var absolutePath = Path.GetFullPath(file);
                if (!referencedFiles.Contains(absolutePath)) unreferencedFiles.Add(absolutePath);
            }
        }

        return new OrphanedFileReport(
            unreferencedFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            missingFiles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            assetsWithoutItems.Order().ToArray());
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

    private static SqliteCommand BuildGalleryCommand(
        SqliteConnection connection,
        string where,
        string tail,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT ci.id, a.hash, a.original_path, a.thumbnail_path, a.medium_thumbnail_path, a.width, a.height, a.format,
                   ci.prompt, ci.notes, ci.category_id, COALESCE(c.name, char(26410,20998,31867)),
                   COALESCE((
                       SELECT GROUP_CONCAT(t.name, ', ')
                       FROM item_tags it
                       JOIN tags t ON t.id = it.tag_id
                       WHERE it.item_id = ci.id
                   ), ''), ci.created_at, ci.deleted_at
            FROM collection_items ci
            JOIN image_assets a ON a.id = ci.asset_id
            LEFT JOIN categories c ON c.id = ci.category_id
            {where}
            {tail};
            """;
        return command;
    }

    private static GalleryItem ReadGalleryItem(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6),
        reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.GetString(11),
        reader.GetString(12), DateTimeOffset.Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)));

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

    private async Task<SearchIndexBackend> InitializeSearchIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await ReadSearchIndexStateAsync(connection, cancellationToken).ConfigureAwait(false);
            if (state.SchemaVersion == SearchIndexSchemaVersion
                && string.Equals(state.Backend, SearchIndexBackend.Trigram.ToString(), StringComparison.OrdinalIgnoreCase)
                && await HasCompleteSearchIndexAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                return SearchIndexBackend.Trigram;
            }

            await RebuildSearchIndexAsync(connection, cancellationToken).ConfigureAwait(false);
            return SearchIndexBackend.Trigram;
        }
        catch (SqliteException ex)
        {
            Trace.TraceWarning($"PromptVault full-text index initialization failed; LIKE fallback will be used: {ex}");
            Diagnostic?.Invoke(this, new RepositoryDiagnostic(
                "search-index",
                "全文搜索索引无法启用，已自动切换到兼容搜索；结果不会丢失，但大图库搜索可能稍慢。",
                ex));
            return SearchIndexBackend.None;
        }
    }

    private static async Task<SearchIndexState> ReadSearchIndexStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, backend
            FROM search_index_state
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SearchIndexState(reader.GetInt32(0), reader.GetString(1))
            : new SearchIndexState(0, "");
    }

    private static async Task<bool> HasCompleteSearchIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE (type = 'table' AND name = 'item_fts')
               OR (type = 'trigger' AND name IN ('item_fts_ai', 'item_fts_ad', 'item_fts_au'));
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0) == 4;
    }

    private static async Task RebuildSearchIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = RebuildFtsTrigramSql;
            command.Parameters.AddWithValue("$schemaVersion", SearchIndexSchemaVersion);
            command.Parameters.AddWithValue("$backend", SearchIndexBackend.Trigram.ToString());
            command.Parameters.AddWithValue("$rebuiltAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private SearchPlan CreateSearchPlan(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SearchPlan(SearchTextMode.None, "");

        var trimmed = query.Trim();
        var canUseTrigram = _searchIndexBackend == SearchIndexBackend.Trigram
            && trimmed.EnumerateRunes().Count() >= 3
            && trimmed.All(character => char.IsLetterOrDigit(character) || character == ' ');
        return canUseTrigram
            ? new SearchPlan(SearchTextMode.Trigram, $"\"{trimmed.Replace("\"", "\"\"")}\"")
            : new SearchPlan(SearchTextMode.Like, trimmed);
    }

    private static bool IsSearchIndexFailure(SqliteException exception) =>
        exception.Message.Contains("item_fts", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("MATCH", StringComparison.OrdinalIgnoreCase);

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

    private const int SearchIndexSchemaVersion = 1;

    private const string RebuildFtsTrigramSql = """
        DROP TRIGGER IF EXISTS item_fts_ai;
        DROP TRIGGER IF EXISTS item_fts_ad;
        DROP TRIGGER IF EXISTS item_fts_au;
        DROP TABLE IF EXISTS item_fts;
        CREATE VIRTUAL TABLE item_fts USING fts5(prompt, notes, content='collection_items', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER item_fts_ai AFTER INSERT ON collection_items BEGIN
          INSERT INTO item_fts(rowid,prompt,notes) VALUES(new.id,new.prompt,new.notes); END;
        CREATE TRIGGER item_fts_ad AFTER DELETE ON collection_items BEGIN
          INSERT INTO item_fts(item_fts,rowid,prompt,notes) VALUES('delete',old.id,old.prompt,old.notes); END;
        CREATE TRIGGER item_fts_au AFTER UPDATE ON collection_items BEGIN
          INSERT INTO item_fts(item_fts,rowid,prompt,notes) VALUES('delete',old.id,old.prompt,old.notes);
          INSERT INTO item_fts(rowid,prompt,notes) VALUES(new.id,new.prompt,new.notes); END;
        INSERT INTO item_fts(item_fts) VALUES('rebuild');
        UPDATE search_index_state
        SET schema_version = $schemaVersion,
            backend = $backend,
            rebuilt_at = $rebuiltAt,
            rebuild_count = rebuild_count + 1
        WHERE id = 1;
        """;

    private enum SearchIndexBackend
    {
        None,
        Trigram
    }

    private enum SearchTextMode
    {
        None,
        Like,
        Trigram
    }

    private sealed record SearchIndexState(int SchemaVersion, string Backend);
    private sealed record SearchPlan(SearchTextMode Mode, string Parameter);
}
