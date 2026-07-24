using PromptVault.Core;
using Microsoft.Data.Sqlite;

namespace PromptVault.Tests;

public sealed class LibraryRepositoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PromptVaultTests", Guid.NewGuid().ToString("N"));
    private LibraryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = new LibraryRepository(new LibraryPaths(_root));
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteCleanup();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SavesAndFindsPromptWithSearch()
    {
        await SaveAsync("hash-a", "cyberpunk city night", "blue light", ["tech style"]);
        var results = await SearchItemsAsync(new SearchOptions(Query: "city night"));
        Assert.Single(results);
        Assert.Equal("cyberpunk city night", results[0].Prompt);
        Assert.Contains("tech style", results[0].Tags);
    }

    [Fact]
    public async Task DuplicateHashUpdatesExistingRecord()
    {
        await SaveAsync("same-hash", "first", "", []);
        var second = await SaveAsync("same-hash", "updated", "new note", ["new-tag"]);
        Assert.True(second.WasDuplicate);
        var results = await SearchItemsAsync(new SearchOptions(Query: "updated"));
        Assert.Single(results);
        Assert.Equal("new note", results[0].Notes);
    }

    [Fact]
    public async Task FullTextSearchSupportsChineseEnglishFragmentsShortWordsAndLiteralSpecialCharacters()
    {
        Assert.True(_repository.FullTextSearchAvailable);
        await SaveAsync("fts-chinese", "赛博朋克城市夜景", "霓虹光影", []);
        await SaveAsync("fts-english", "cinematic cyberpunk portrait", "volumetric lighting", []);
        await SaveAsync("fts-short", "QZ character study", "", []);
        await SaveAsync("fts-special", "render at 100% with foo_bar and C++", "", []);
        await SaveAsync("fts-special-decoy", "render at 1000 with fooXbar", "", []);

        Assert.Equal("赛博朋克城市夜景",
            Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "博朋克"))).Prompt);
        Assert.Equal("cinematic cyberpunk portrait",
            Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "BERPUNK"))).Prompt);
        Assert.Equal("QZ character study",
            Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "QZ"))).Prompt);
        Assert.Equal("render at 100% with foo_bar and C++",
            Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "100%"))).Prompt);
        Assert.Equal("render at 100% with foo_bar and C++",
            Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "foo_bar"))).Prompt);
        Assert.Equal("render at 100% with foo_bar and C++",
            Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "C++"))).Prompt);
    }

    [Fact]
    public async Task FullTextIndexTracksPromptAndNotesUpdates()
    {
        await SaveAsync("fts-update", "original phrase marker", "", []);
        Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "original")));

        await SaveAsync("fts-update", "updated phrase marker", "", []);
        Assert.Empty(await SearchItemsAsync(new SearchOptions(Query: "original")));
        var updated = Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "updated")));

        await _repository.UpdateItemsMetadataAsync([updated.Id], null, "volumetric glow marker");
        Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "volumetric")));
    }

    [Fact]
    public async Task FullTextFailureFallsBackToCorrectLikeSearchWithDiagnostic()
    {
        await SaveAsync("fts-fallback", "fallback searchable prompt", "", []);
        RepositoryDiagnostic? diagnostic = null;
        _repository.Diagnostic += (_, value) => diagnostic = value;
        await ExecuteSqlAsync(_repository.Paths.Database, "DROP TABLE item_fts;");

        var result = Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "searchable")));

        Assert.Equal("fallback searchable prompt", result.Prompt);
        Assert.False(_repository.FullTextSearchAvailable);
        Assert.NotNull(diagnostic);
        Assert.Equal("search-index", diagnostic!.Area);
        Assert.Contains("兼容搜索", diagnostic.Message);
    }

    [Fact]
    public async Task SearchPageReturnsTotalAndStableCursorPages()
    {
        var ids = new List<long>();
        for (var index = 0; index < 7; index++)
        {
            ids.Add((await SaveAsync($"cursor-{index}", $"item {index}", "", [])).ItemId);
        }

        await ExecuteSqlAsync(
            _repository.Paths.Database,
            "UPDATE collection_items SET created_at = '2026-07-24T08:00:00.0000000+00:00';");

        var seen = new List<long>();
        GalleryPageCursor? cursor = null;
        do
        {
            var page = await _repository.SearchPageAsync(new SearchOptions(
                Sort: GallerySortOrder.NewestFirst,
                PageSize: 3,
                Cursor: cursor));
            Assert.Equal(7, page.TotalCount);
            seen.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(ids.OrderByDescending(id => id), seen);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task SearchPageSupportsOldestFirstCursorOrder()
    {
        var ids = new List<long>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add((await SaveAsync($"oldest-{index}", $"oldest {index}", "", [])).ItemId);
        }

        await ExecuteSqlAsync(
            _repository.Paths.Database,
            "UPDATE collection_items SET created_at = '2026-07-24T08:00:00.0000000+00:00';");

        var first = await _repository.SearchPageAsync(new SearchOptions(
            Sort: GallerySortOrder.OldestFirst,
            PageSize: 2));
        var second = await _repository.SearchPageAsync(new SearchOptions(
            Sort: GallerySortOrder.OldestFirst,
            PageSize: 2,
            Cursor: first.NextCursor));
        var third = await _repository.SearchPageAsync(new SearchOptions(
            Sort: GallerySortOrder.OldestFirst,
            PageSize: 2,
            Cursor: second.NextCursor));

        Assert.Equal(ids, first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id));
        Assert.NotNull(first.NextCursor);
        Assert.NotNull(second.NextCursor);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task SearchOptionsUnifyCategoryTrashTagSourceAndSortFilters()
    {
        var categoryId = await _repository.AddCategoryAsync("unified-filter", "");
        await SaveAsync("filter-category", "category", "", ["blue"], categoryId);
        await SaveAsync("filter-unclassified", "unclassified", "", ["blue"]);
        var trashed = await SaveAsync("filter-trash", "trash", "", ["red"]);
        await _repository.MoveToTrashAsync(trashed.ItemId);

        var category = await _repository.SearchPageAsync(new SearchOptions(CategoryId: categoryId));
        var unclassified = await _repository.SearchPageAsync(new SearchOptions(UncategorizedOnly: true));
        var tag = await _repository.SearchPageAsync(new SearchOptions(Tag: "blue"));
        var trash = await _repository.SearchPageAsync(new SearchOptions(Trash: GalleryTrashScope.Trash));

        Assert.Equal(1, category.TotalCount);
        Assert.Equal("category", Assert.Single(category.Items).Prompt);
        Assert.Equal(1, unclassified.TotalCount);
        Assert.Equal("unclassified", Assert.Single(unclassified.Items).Prompt);
        Assert.Equal(2, tag.TotalCount);
        Assert.Equal(1, trash.TotalCount);
        Assert.Equal("trash", Assert.Single(trash.Items).Prompt);
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.SearchPageAsync(
            new SearchOptions(Source: GallerySourceKind.ExternalFolder, SourceId: "folder")));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.SearchPageAsync(
            new SearchOptions(CategoryId: categoryId, UncategorizedOnly: true)));
    }

    [Fact]
    public async Task TrashCanBeRestored()
    {
        var saved = await SaveAsync("trash-hash", "trash me", "", []);
        await _repository.MoveToTrashAsync(saved.ItemId);
        Assert.Empty(await SearchItemsAsync(new SearchOptions()));
        Assert.Single(await SearchItemsAsync(new SearchOptions(Trash: GalleryTrashScope.Trash)));
        await _repository.RestoreAsync(saved.ItemId);
        Assert.Single(await SearchItemsAsync(new SearchOptions()));
    }

    [Fact]
    public async Task PermanentlyDeletingTrashRemovesRecordAndFiles()
    {
        const string hash = "permanent-delete-hash";
        var saved = await SaveAsync(hash, "delete forever", "", []);
        var files = new[]
        {
            _repository.Paths.ToAbsolute($"originals/{hash}.png"),
            _repository.Paths.ToAbsolute($"thumbnails/small/{hash}.jpg"),
            _repository.Paths.ToAbsolute($"thumbnails/medium/{hash}.jpg")
        };
        foreach (var file in files) await File.WriteAllTextAsync(file, "test");

        await _repository.MoveToTrashAsync(saved.ItemId);
        await _repository.PermanentlyDeleteTrashItemsAsync([saved.ItemId]);

        Assert.Empty(await SearchItemsAsync(new SearchOptions(Trash: GalleryTrashScope.Trash)));
        Assert.All(files, file => Assert.False(File.Exists(file)));
    }

    [Fact]
    public async Task PurgingExpiredTrashRemovesAssetAndAllFiles()
    {
        const string hash = "expired-trash-hash";
        var saved = await SaveAsync(hash, "expired", "", []);
        var files = CreateAssetFiles(hash);
        await _repository.MoveToTrashAsync(saved.ItemId);
        await ExecuteSqlAsync(
            _repository.Paths.Database,
            "UPDATE collection_items SET deleted_at = '2020-01-01T00:00:00.0000000+00:00' WHERE id = $id;",
            ("$id", saved.ItemId));

        var result = await _repository.PurgeTrashAsync(30);

        Assert.Equal(1, result.DeletedAssets);
        Assert.Equal(3, result.DeletedFiles);
        Assert.Empty(result.FileDeletionFailures);
        Assert.Empty(await SearchItemsAsync(new SearchOptions(Trash: GalleryTrashScope.Trash)));
        Assert.All(files, file => Assert.False(File.Exists(file)));
        Assert.Equal(0L, await ExecuteScalarAsync(
            _repository.Paths.Database,
            "SELECT COUNT(*) FROM image_assets WHERE hash = $hash;",
            ("$hash", hash)));
    }

    [Fact]
    public async Task PurgingTrashKeepsRecentAndActiveItems()
    {
        var recent = await SaveAsync("recent-trash-hash", "recent", "", []);
        var active = await SaveAsync("active-hash", "active", "", []);
        var recentFiles = CreateAssetFiles("recent-trash-hash");
        var activeFiles = CreateAssetFiles("active-hash");
        await _repository.MoveToTrashAsync(recent.ItemId);

        var result = await _repository.PurgeTrashAsync(30);

        Assert.Equal(0, result.DeletedAssets);
        Assert.Single(await SearchItemsAsync(new SearchOptions(Trash: GalleryTrashScope.Trash)));
        Assert.Single(await SearchItemsAsync(new SearchOptions(Query: "active")));
        Assert.All(recentFiles.Concat(activeFiles), file => Assert.True(File.Exists(file)));

        var activeDelete = await _repository.PermanentlyDeleteTrashItemsAsync([active.ItemId]);
        Assert.Equal(0, activeDelete.DeletedAssets);
        Assert.All(activeFiles, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public async Task OrphanInspectionReportsWithoutDeleting()
    {
        await SaveAsync("missing-files-hash", "missing", "", []);
        var orphanPath = Path.Combine(_repository.Paths.Originals, "unreferenced.png");
        await File.WriteAllTextAsync(orphanPath, "orphan");
        await ExecuteSqlAsync(
            _repository.Paths.Database,
            """
            INSERT INTO image_assets(
                hash, original_path, thumbnail_path, medium_thumbnail_path, width, height, format, created_at)
            VALUES(
                'asset-without-item', 'originals/orphan-record.png', 'thumbnails/small/orphan-record.jpg',
                'thumbnails/medium/orphan-record.jpg', 1, 1, 'png', '2020-01-01T00:00:00+00:00');
            """);

        var report = await _repository.InspectOrphanedFilesAsync();

        Assert.Contains(Path.GetFullPath(orphanPath), report.UnreferencedFiles);
        Assert.Contains("originals/missing-files-hash.png", report.MissingReferencedFiles);
        Assert.Single(report.AssetsWithoutItems);
        Assert.True(File.Exists(orphanPath));
    }

    [Fact]
    public async Task VersionOneDatabaseIsBackedUpAndMigrated()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultMigrationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LibraryPaths(root);
            var initial = new LibraryRepository(paths);
            await initial.InitializeAsync();
            SqliteConnection.ClearAllPools();
            await ExecuteSqlAsync(paths.Database, """
                DROP INDEX IF EXISTS ix_items_category_created;
                DROP INDEX IF EXISTS ix_items_trash_created;
                DROP INDEX IF EXISTS ix_items_deleted;
                UPDATE schema_info SET version = 1;
                PRAGMA user_version = 1;
                """);

            var upgraded = new LibraryRepository(paths);
            await upgraded.InitializeAsync();

            Assert.NotNull(upgraded.LastMigration);
            Assert.Equal(1, upgraded.LastMigration!.FromVersion);
            Assert.Equal(5, upgraded.LastMigration.ToVersion);
            Assert.True(File.Exists(upgraded.LastMigration.BackupPath));
            Assert.Equal(5L, await ExecuteScalarAsync(paths.Database, "SELECT version FROM schema_info;"));
            Assert.Equal(1L, await ExecuteScalarAsync(
                upgraded.LastMigration.BackupPath!,
                "SELECT version FROM schema_info;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task VersionTwoDatabaseIsBackedUpBeforeAddingPagingIndexes()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultPagingMigrationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LibraryPaths(root);
            var initial = new LibraryRepository(paths);
            await initial.InitializeAsync();
            SqliteConnection.ClearAllPools();
            await ExecuteSqlAsync(paths.Database, """
                DROP INDEX IF EXISTS ix_items_category_created;
                DROP INDEX IF EXISTS ix_items_trash_created;
                UPDATE schema_info SET version = 2;
                PRAGMA user_version = 2;
                """);

            var upgraded = new LibraryRepository(paths);
            await upgraded.InitializeAsync();

            Assert.Equal(2, upgraded.LastMigration!.FromVersion);
            Assert.Equal(5, upgraded.LastMigration.ToVersion);
            Assert.True(File.Exists(upgraded.LastMigration.BackupPath));
            Assert.Equal(2L, await ExecuteScalarAsync(
                upgraded.LastMigration.BackupPath!,
                "SELECT version FROM schema_info;"));
            Assert.Equal(2L, await ExecuteScalarAsync(
                paths.Database,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'index'
                  AND name IN ('ix_items_trash_created', 'ix_items_category_created');
                """));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task VersionThreeMigrationBuildsFullTextIndexOnceAndNormalStartupDoesNotRebuild()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultFtsMigrationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LibraryPaths(root);
            var initial = new LibraryRepository(paths);
            await initial.InitializeAsync();
            await initial.SaveAsync(new SaveItemInput(
                new AssetInput(
                    "migration-fts",
                    "originals/migration-fts.png",
                    "thumbnails/small/migration-fts.jpg",
                    "thumbnails/medium/migration-fts.jpg",
                    100,
                    100,
                    "png"),
                "赛博朋克 migration searchable",
                "",
                null,
                []));
            SqliteConnection.ClearAllPools();
            await ExecuteSqlAsync(paths.Database, """
                DROP TRIGGER IF EXISTS item_fts_ai;
                DROP TRIGGER IF EXISTS item_fts_ad;
                DROP TRIGGER IF EXISTS item_fts_au;
                DROP TABLE IF EXISTS item_fts;
                DROP TABLE IF EXISTS search_index_state;
                UPDATE schema_info SET version = 3;
                PRAGMA user_version = 3;
                """);

            var upgraded = new LibraryRepository(paths);
            await upgraded.InitializeAsync();

            Assert.Equal(3, upgraded.LastMigration!.FromVersion);
            Assert.Equal(5, upgraded.LastMigration.ToVersion);
            Assert.True(File.Exists(upgraded.LastMigration.BackupPath));
            Assert.True(upgraded.FullTextSearchAvailable);
            Assert.Equal(1L, await ExecuteScalarAsync(
                paths.Database,
                """
                SELECT COUNT(*)
                FROM search_index_state
                WHERE id = 1
                  AND schema_version = 1
                  AND backend = 'Trigram'
                  AND rebuild_count = 1;
                """));
            Assert.Equal(3L, await ExecuteScalarAsync(
                upgraded.LastMigration.BackupPath!,
                "SELECT version FROM schema_info;"));
            Assert.Equal(1L, await ExecuteScalarAsync(
                upgraded.LastMigration.BackupPath!,
                "SELECT COUNT(*) FROM collection_items WHERE prompt = '赛博朋克 migration searchable';"));
            Assert.Equal(0L, await ExecuteScalarAsync(
                upgraded.LastMigration.BackupPath!,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'search_index_state';"));
            Assert.Single((await upgraded.SearchPageAsync(new SearchOptions(Query: "博朋克"))).Items);

            SqliteConnection.ClearAllPools();
            var restarted = new LibraryRepository(paths);
            await restarted.InitializeAsync();

            Assert.True(restarted.FullTextSearchAvailable);
            Assert.Equal(1L, await ExecuteScalarAsync(
                paths.Database,
                "SELECT rebuild_count FROM search_index_state WHERE id = 1;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task FailedMigrationKeepsOriginalAndRecoverableBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultMigrationFailureTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LibraryPaths(root);
            var initial = new LibraryRepository(paths);
            await initial.InitializeAsync();
            SqliteConnection.ClearAllPools();
            await ExecuteSqlAsync(paths.Database, """
                PRAGMA foreign_keys = OFF;
                DROP TABLE collection_items;
                CREATE TABLE collection_items(id INTEGER PRIMARY KEY);
                DROP INDEX IF EXISTS ix_items_deleted;
                UPDATE schema_info SET version = 1;
                PRAGMA user_version = 1;
                """);

            var failing = new LibraryRepository(paths);
            var error = await Assert.ThrowsAsync<DatabaseMigrationException>(() => failing.InitializeAsync());

            Assert.Equal(1, error.FromVersion);
            Assert.True(File.Exists(error.BackupPath));
            Assert.Equal(1L, await ExecuteScalarAsync(paths.Database, "SELECT version FROM schema_info;"));
            Assert.Equal(1L, await ExecuteScalarAsync(error.BackupPath!, "SELECT version FROM schema_info;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
    [Fact]
    public async Task DeletingCategoryMovesItemToUnclassified()
    {
        var categoryId = await _repository.AddCategoryAsync("test-category", "test");
        await SaveAsync("category-hash", "category", "", [], categoryId);
        await _repository.DeleteCategoryAsync(categoryId);
        var item = Assert.Single(await SearchItemsAsync(new SearchOptions()));
        Assert.Null(item.CategoryId);
        Assert.Equal("未分类", item.CategoryName);
    }

    [Fact]
    public async Task CanMoveSavedItemsToAnotherCategory()
    {
        var categoryId = await _repository.AddCategoryAsync("move-target", "target");
        var first = await SaveAsync("move-hash-a", "move a", "", []);
        var second = await SaveAsync("move-hash-b", "move b", "", []);
        await _repository.UpdateItemsCategoryAsync([first.ItemId, second.ItemId], categoryId);

        var results = await SearchItemsAsync(new SearchOptions(CategoryId: categoryId));
        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.Equal("move-target", item.CategoryName));
    }

    [Fact]
    public async Task CanMoveCategoryOrderUpAndDown()
    {
        var first = await _repository.AddCategoryAsync("order-a", "");
        var second = await _repository.AddCategoryAsync("order-b", "");
        var third = await _repository.AddCategoryAsync("order-c", "");

        await _repository.MoveCategoryAsync(third, -1);
        var afterMoveUp = await _repository.GetCategoriesAsync();
        Assert.True(IndexOf(afterMoveUp, third) < IndexOf(afterMoveUp, second));

        await _repository.MoveCategoryAsync(first, 1);
        var afterMoveDown = await _repository.GetCategoriesAsync();
        Assert.True(IndexOf(afterMoveDown, first) > IndexOf(afterMoveDown, third));
    }
    [Fact]
    public async Task TagSearchSupportsPartialAndDelimitedTerms()
    {
        await SaveAsync("tag-search-a", "pants prompt", "", ["wide pants", "blue"]);
        await SaveAsync("tag-search-b", "shirt prompt", "", ["shirt", "red"]);

        var partial = await SearchItemsAsync(new SearchOptions(Tag: "pant"));
        Assert.Single(partial);
        Assert.Equal("pants prompt", partial[0].Prompt);

        var multiple = await SearchItemsAsync(new SearchOptions(Tag: "pant, blue"));
        Assert.Single(multiple);
        Assert.Equal("pants prompt", multiple[0].Prompt);
    }

    [Fact]
    public async Task CanUpdateTagsAndNotesForMultipleItems()
    {
        var first = await SaveAsync("metadata-a", "metadata a", "old", ["old"]);
        var second = await SaveAsync("metadata-b", "metadata b", "old", ["old"]);

        await _repository.UpdateItemsMetadataAsync([first.ItemId, second.ItemId], "batch, edited", "shared note");

        var results = await SearchItemsAsync(new SearchOptions(Tag: "edit"));
        Assert.Equal(2, results.Count);
        Assert.All(results, item =>
        {
            Assert.Equal("shared note", item.Notes);
            Assert.Contains("batch", item.Tags);
            Assert.Contains("edited", item.Tags);
        });
    }

    [Fact]
    public async Task ContentHasherIsStable()
    {
        await using var first = new MemoryStream("hello"u8.ToArray());
        await using var second = new MemoryStream("hello"u8.ToArray());
        Assert.Equal(await ContentHasher.Sha256Async(first), await ContentHasher.Sha256Async(second));
    }


    private static int IndexOf(IReadOnlyList<CategoryRecord> categories, long id)
    {
        for (var i = 0; i < categories.Count; i++) if (categories[i].Id == id) return i;
        return -1;
    }

    private async Task<IReadOnlyList<GalleryItem>> SearchItemsAsync(SearchOptions options)
    {
        var page = await _repository.SearchPageAsync(options with { PageSize = 1000 });
        return page.Items;
    }

    private Task<SaveResult> SaveAsync(string hash, string prompt, string notes, IReadOnlyList<string> tags, long? category = null) =>
        _repository.SaveAsync(new SaveItemInput(
            new AssetInput(hash, $"originals/{hash}.png", $"thumbnails/small/{hash}.jpg", $"thumbnails/medium/{hash}.jpg", 100, 100, "png"),
            prompt, notes, category, tags));

    private string[] CreateAssetFiles(string hash)
    {
        var files = new[]
        {
            _repository.Paths.ToAbsolute($"originals/{hash}.png"),
            _repository.Paths.ToAbsolute($"thumbnails/small/{hash}.jpg"),
            _repository.Paths.ToAbsolute($"thumbnails/medium/{hash}.jpg")
        };
        foreach (var file in files) File.WriteAllText(file, "test");
        return files;
    }

    private static async Task ExecuteSqlAsync(
        string database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(
        string database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private void SqliteCleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
