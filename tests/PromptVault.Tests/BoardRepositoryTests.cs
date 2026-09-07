using Microsoft.Data.Sqlite;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardRepositoryTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "PromptVaultBoardTests", Guid.NewGuid().ToString("N"));
    private LibraryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = new LibraryRepository(new LibraryPaths(_root));
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
    public async Task MultipleNamedBoardsAreIndependentAndNamesAreUnique()
    {
        var first = await _repository.CreateBoardAsync("灵感墙");
        var second = await _repository.CreateBoardAsync("项目 A");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(["项目 A", "灵感墙"], (await _repository.GetBoardsAsync()).Select(x => x.Name));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.CreateBoardAsync("灵感墙"));
    }

    [Fact]
    public async Task ImageReferenceTransformCropAndGroupSurviveRestart()
    {
        var saved = await SaveItemAsync("board-persist");
        var board = await _repository.CreateBoardAsync("恢复测试");
        var group = await _repository.CreateBoardGroupAsync(board.Id, "主体");
        var added = Assert.Single(await _repository.AddBoardItemsAsync(
            board.Id,
            [
                new BoardItemPlacementInput(
                    saved.ItemId,
                    112.5,
                    -36.25,
                    480,
                    320,
                    7,
                    12.5,
                    0.1,
                    0.05,
                    0.2,
                    0.15,
                    group.Id)
            ]));
        await _repository.UpdateBoardViewAsync(board.Id, 901.5, -210.25, 1.75);
        await _repository.UpdateBoardItemAsync(
            board.Id,
            new BoardItemUpdate(
                added.Id,
                150,
                -20,
                512,
                288,
                9,
                -8,
                0.12,
                0.07,
                0.18,
                0.09,
                group.Id,
                @"D:\moved\board-persist.png"));

        SqliteConnection.ClearAllPools();
        var restarted = new LibraryRepository(new LibraryPaths(_root));
        await restarted.InitializeAsync();
        var document = Assert.IsType<BoardDocument>(await restarted.GetBoardDocumentAsync(board.Id));

        Assert.Equal("恢复测试", document.Board.Name);
        Assert.Equal(901.5, document.Board.ViewOffsetX);
        Assert.Equal(-210.25, document.Board.ViewOffsetY);
        Assert.Equal(1.75, document.Board.Zoom);
        Assert.Equal("主体", Assert.Single(document.Groups).Name);
        var item = Assert.Single(document.Items);
        Assert.NotNull(item.AssetId);
        Assert.Equal("originals/board-persist.png", item.SourcePathSnapshot);
        Assert.Equal(@"D:\moved\board-persist.png", item.SourcePathOverride);
        Assert.Equal((150d, -20d, 512d, 288d), (item.X, item.Y, item.Width, item.Height));
        Assert.Equal((9, -8d), (item.ZIndex, item.Rotation));
        Assert.Equal((0.12, 0.07, 0.18, 0.09), (item.CropLeft, item.CropTop, item.CropRight, item.CropBottom));
        Assert.Equal(group.Id, item.GroupId);
    }

    [Fact]
    public async Task BatchTransformUpdateCommitsEverySelectedItemAtomically()
    {
        var first = await SaveItemAsync("board-batch-first");
        var second = await SaveItemAsync("board-batch-second");
        var board = await _repository.CreateBoardAsync("batch-transform");
        var items = await _repository.AddBoardItemsAsync(
            board.Id,
            [
                new BoardItemPlacementInput(first.ItemId, 0, 0, 300, 200),
                new BoardItemPlacementInput(second.ItemId, 340, 0, 300, 200)
            ]);

        await _repository.UpdateBoardItemsAsync(
            board.Id,
            items.Select((item, index) => new BoardItemUpdate(
                item.Id,
                item.X + 25 + index,
                item.Y + 40,
                item.Width * 1.25,
                item.Height * 1.25,
                item.ZIndex,
                15 + index,
                0.05,
                0.04,
                0.03,
                0.02,
                item.GroupId,
                item.SourcePathOverride)).ToArray());

        var persisted = await _repository.GetBoardItemsAsync(board.Id);
        Assert.All(persisted, item => Assert.Equal(375, item.Width));
        Assert.Equal([25d, 366d], persisted.OrderBy(item => item.Id).Select(item => item.X));
        Assert.Equal([15d, 16d], persisted.OrderBy(item => item.Id).Select(item => item.Rotation));
        Assert.All(persisted, item => Assert.Equal(0.05, item.CropLeft));
    }

    [Fact]
    public async Task BatchTransformUpdateRejectsDuplicatesBeforeWriting()
    {
        var saved = await SaveItemAsync("board-batch-duplicate");
        var board = await _repository.CreateBoardAsync("batch-duplicate");
        var item = Assert.Single(await _repository.AddBoardItemsAsync(
            board.Id,
            [new BoardItemPlacementInput(saved.ItemId, 10, 20, 300, 200)]));
        var update = new BoardItemUpdate(
            item.Id, 90, 80, 500, 400, item.ZIndex, item.Rotation,
            item.CropLeft, item.CropTop, item.CropRight, item.CropBottom,
            item.GroupId, item.SourcePathOverride);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.UpdateBoardItemsAsync(board.Id, [update, update]));

        var persisted = Assert.Single(await _repository.GetBoardItemsAsync(board.Id));
        Assert.Equal((10d, 20d, 300d, 200d),
            (persisted.X, persisted.Y, persisted.Width, persisted.Height));
    }

    [Fact]
    public async Task BoardDeletionNeverDeletesLibraryAssetOrSourceFile()
    {
        var saved = await SaveItemAsync("board-safe-delete", createFiles: true);
        var sourcePath = _repository.Paths.ToAbsolute("originals/board-safe-delete.png");
        var board = await _repository.CreateBoardAsync("可安全删除");
        await _repository.AddBoardItemsAsync(
            board.Id,
            [new BoardItemPlacementInput(saved.ItemId, 0, 0, 300, 200)]);

        await _repository.DeleteBoardAsync(board.Id);

        Assert.Null(await _repository.GetBoardDocumentAsync(board.Id));
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(saved.ItemId, Assert.Single(
            (await _repository.SearchPageAsync(new SearchOptions(PageSize: 10))).Items).Id);
    }

    [Fact]
    public async Task MissingAssetKeepsBoardReferenceSnapshotRecoverable()
    {
        var saved = await SaveItemAsync("board-missing");
        var board = await _repository.CreateBoardAsync("丢失源文件");
        await _repository.AddBoardItemsAsync(
            board.Id,
            [new BoardItemPlacementInput(saved.ItemId, 0, 0, 300, 200)]);

        await ExecuteSqlAsync(
            """
            DELETE FROM collection_items WHERE id = $item;
            DELETE FROM image_assets WHERE hash = 'board-missing';
            """,
            ("$item", saved.ItemId));

        var item = Assert.Single(await _repository.GetBoardItemsAsync(board.Id));
        Assert.Null(item.AssetId);
        Assert.Null(item.OriginalPath);
        Assert.Equal("originals/board-missing.png", item.SourcePathSnapshot);

        await _repository.UpdateBoardItemAsync(
            board.Id,
            new BoardItemUpdate(
                item.Id,
                item.X,
                item.Y,
                item.Width,
                item.Height,
                item.ZIndex,
                item.Rotation,
                item.CropLeft,
                item.CropTop,
                item.CropRight,
                item.CropBottom,
                item.GroupId,
                @"E:\relinked\board-missing.png"));
        Assert.Equal(
            @"E:\relinked\board-missing.png",
            Assert.Single(await _repository.GetBoardItemsAsync(board.Id)).SourcePathOverride);
    }

    [Fact]
    public async Task GroupCannotCrossBoardBoundary()
    {
        var saved = await SaveItemAsync("board-group-boundary");
        var first = await _repository.CreateBoardAsync("甲");
        var second = await _repository.CreateBoardAsync("乙");
        var secondGroup = await _repository.CreateBoardGroupAsync(second.Id, "乙组");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.AddBoardItemsAsync(
                first.Id,
                [new BoardItemPlacementInput(saved.ItemId, 0, 0, 100, 100, GroupId: secondGroup.Id)]));
    }

    [Fact]
    public async Task RenameGroupAndSnapshotRestoreSurviveRepositoryRestart()
    {
        var first = await SaveItemAsync("board-snapshot-first");
        var second = await SaveItemAsync("board-snapshot-second");
        var board = await _repository.CreateBoardAsync("快照前");
        var group = await _repository.CreateBoardGroupAsync(board.Id, "旧组名");
        var original = await _repository.AddBoardItemsAsync(
            board.Id,
            [
                new BoardItemPlacementInput(first.ItemId, 10, 20, 320, 180, GroupId: group.Id),
                new BoardItemPlacementInput(second.ItemId, 360, 20, 320, 180, GroupId: group.Id)
            ]);
        await _repository.RenameBoardAsync(board.Id, "快照恢复");
        await _repository.RenameBoardGroupAsync(board.Id, group.Id, "重启分组");
        await _repository.DeleteBoardItemsAsync(board.Id, [original[0].Id]);
        Assert.Single(await _repository.GetBoardItemsAsync(board.Id));

        await _repository.ReplaceBoardItemsAsync(board.Id, original);
        SqliteConnection.ClearAllPools();
        var restarted = new LibraryRepository(new LibraryPaths(_root));
        await restarted.InitializeAsync();
        var document = Assert.IsType<BoardDocument>(await restarted.GetBoardDocumentAsync(board.Id));

        Assert.Equal("快照恢复", document.Board.Name);
        Assert.Equal("重启分组", Assert.Single(document.Groups).Name);
        Assert.Equal(2, document.Items.Count);
        Assert.All(document.Items, item => Assert.Equal(group.Id, item.GroupId));
    }

    [Fact]
    public async Task NotesAndBackgroundSurviveRestartAndRemainBoardScoped()
    {
        var board = await _repository.CreateBoardAsync("便签持久化");
        await _repository.UpdateBoardBackgroundAsync(board.Id, "blueprint");
        var note = await _repository.AddBoardNoteAsync(
            board.Id,
            "构图方向\n- 保留留白",
            -120,
            75,
            360,
            240,
            4,
            "rose");
        await _repository.UpdateBoardNoteAsync(
            board.Id,
            new BoardNoteUpdate(
                note.Id,
                "构图方向\n- 保留留白\n- 降低饱和度",
                -80,
                90,
                420,
                280,
                9,
                "blue"));

        SqliteConnection.ClearAllPools();
        var restarted = new LibraryRepository(new LibraryPaths(_root));
        await restarted.InitializeAsync();
        var document = Assert.IsType<BoardDocument>(await restarted.GetBoardDocumentAsync(board.Id));

        Assert.Equal("blueprint", document.Board.BackgroundStyle);
        var restored = Assert.Single(document.Notes);
        Assert.Equal(note.Id, restored.Id);
        Assert.Equal("构图方向\n- 保留留白\n- 降低饱和度", restored.Text);
        Assert.Equal((-80d, 90d, 420d, 280d), (restored.X, restored.Y, restored.Width, restored.Height));
        Assert.Equal(9, restored.ZIndex);
        Assert.Equal("blue", restored.ColorStyle);
    }

    [Fact]
    public async Task ReplaceBoardNotesRestoresAddedDeletedAndResizedNotes()
    {
        var board = await _repository.CreateBoardAsync("note snapshot restore");
        var first = await _repository.AddBoardNoteAsync(
            board.Id, "first", 10, 20, 240, 160, 2, "yellow");
        var second = await _repository.AddBoardNoteAsync(
            board.Id, "second", 300, 40, 280, 180, 3, "blue");
        var snapshot = await _repository.GetBoardNotesAsync(board.Id);

        await _repository.UpdateBoardNoteAsync(
            board.Id,
            new BoardNoteUpdate(first.Id, "resized", 80, 90, 640, 480, 8, "rose"));
        await _repository.DeleteBoardNotesAsync(board.Id, [second.Id]);
        var transient = await _repository.AddBoardNoteAsync(
            board.Id, "transient", -200, -100, 320, 200, 9, "slate");

        await _repository.ReplaceBoardNotesAsync(board.Id, snapshot);
        var restarted = new LibraryRepository(new LibraryPaths(_root));
        await restarted.InitializeAsync();
        var restored = (await restarted.GetBoardNotesAsync(board.Id)).OrderBy(note => note.Id).ToArray();

        Assert.Equal(2, restored.Length);
        Assert.Equal([first.Id, second.Id], restored.Select(note => note.Id));
        Assert.DoesNotContain(restored, note => note.Text == transient.Text);
        Assert.Equal((10d, 20d, 240d, 160d),
            (restored[0].X, restored[0].Y, restored[0].Width, restored[0].Height));
        Assert.Equal("first", restored[0].Text);
        Assert.Equal((300d, 40d, 280d, 180d),
            (restored[1].X, restored[1].Y, restored[1].Width, restored[1].Height));
        Assert.Equal("second", restored[1].Text);
    }

    [Fact]
    public async Task DeletingBoardCascadesNotesOnly()
    {
        var saved = await SaveItemAsync("board-note-safe", createFiles: true);
        var sourcePath = _repository.Paths.ToAbsolute("originals/board-note-safe.png");
        var board = await _repository.CreateBoardAsync("便签删除边界");
        await _repository.AddBoardItemsAsync(
            board.Id,
            [new BoardItemPlacementInput(saved.ItemId, 0, 0, 300, 200)]);
        await _repository.AddBoardNoteAsync(board.Id, "仅属于画板", 20, 30);

        await _repository.DeleteBoardAsync(board.Id);

        Assert.Empty(await _repository.GetBoardNotesAsync(board.Id));
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(saved.ItemId, Assert.Single(
            (await _repository.SearchPageAsync(new SearchOptions(PageSize: 10))).Items).Id);
    }

    [Fact]
    public async Task SceneRestoreRollsBackImagesWhenNoteRestoreFails()
    {
        var saved = await SaveItemAsync("atomic-scene");
        var board = await _repository.CreateBoardAsync("原子恢复");
        var item = Assert.Single(await _repository.AddBoardItemsAsync(board.Id, [new BoardItemPlacementInput(saved.ItemId, 10, 20, 300, 200)]));
        var note = await _repository.AddBoardNoteAsync(board.Id, "original", 30, 40);
        await ExecuteSqlAsync("CREATE TRIGGER reject_notes BEFORE INSERT ON board_notes BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => _repository.ReplaceBoardSceneAsync(board.Id, [item with { X = 900 }], [note with { Text = "replacement" }]));
        var failed = (await _repository.GetBoardDocumentAsync(board.Id))!;
        Assert.Equal(10, Assert.Single(failed.Items).X);
        Assert.Equal("original", Assert.Single(failed.Notes).Text);
        await ExecuteSqlAsync("DROP TRIGGER reject_notes;");
        await _repository.ReplaceBoardSceneAsync(board.Id, [item with { X = 900 }], [note with { Text = "replacement" }]);
        var restored = (await _repository.GetBoardDocumentAsync(board.Id))!;
        Assert.Equal(900, Assert.Single(restored.Items).X);
        Assert.Equal("replacement", Assert.Single(restored.Notes).Text);
    }

    [Fact]
    public async Task PendingChangesSurviveFailureAndRetryForTheirOwnBoards()
    {
        var saved = await SaveItemAsync("pending-scene");
        var first = await _repository.CreateBoardAsync("待保存 A");
        var second = await _repository.CreateBoardAsync("待保存 B");
        var item = Assert.Single(await _repository.AddBoardItemsAsync(first.Id, [new BoardItemPlacementInput(saved.ItemId, 10, 20, 300, 200)]));
        var note = await _repository.AddBoardNoteAsync(second.Id, "original", 30, 40);
        var pending = new PromptVault.App.Services.BoardPendingSaves(_repository);
        pending.EnqueueItems(first.Id, [new BoardItemUpdate(item.Id, 500, 20, 300, 200, 0, 0, 0, 0, 0, 0, null, null)]);
        pending.EnqueueNotes(second.Id, [new BoardNoteUpdate(note.Id, "edited", 30, 40, 300, 200, 0, note.ColorStyle)]);
        pending.EnqueueView(second.Id, 123, 456, 1.5);
        await ExecuteSqlAsync("CREATE TRIGGER reject_items BEFORE UPDATE ON board_items BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => pending.FlushAsync());
        Assert.True(pending.HasPending);
        Assert.Equal(10, Assert.Single(await _repository.GetBoardItemsAsync(first.Id)).X);
        pending.EnqueueItems(first.Id, [new BoardItemUpdate(item.Id, 700, 20, 300, 200, 0, 0, 0, 0, 0, 0, null, null)]);
        await ExecuteSqlAsync("DROP TRIGGER reject_items;");
        await Task.WhenAll(pending.FlushAsync(), pending.FlushAsync());
        Assert.False(pending.HasPending);
        Assert.Equal(700, Assert.Single(await _repository.GetBoardItemsAsync(first.Id)).X);
        var updatedSecond = (await _repository.GetBoardDocumentAsync(second.Id))!;
        Assert.Equal("edited", Assert.Single(updatedSecond.Notes).Text);
        Assert.Equal(123, updatedSecond.Board.ViewOffsetX);
        Assert.Equal(1.5, updatedSecond.Board.Zoom);
        pending.EnqueueView(first.Id, 300, 400, 2);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.FlushAsync(canceled.Token));
        Assert.True(pending.HasPending);
        await pending.FlushAsync();
        Assert.False(pending.HasPending);
    }

    private async Task<SaveResult> SaveItemAsync(string hash, bool createFiles = false)
    {
        if (createFiles)
        {
            foreach (var relative in new[]
                     {
                         $"originals/{hash}.png",
                         $"thumbnails/small/{hash}.jpg",
                         $"thumbnails/medium/{hash}.jpg"
                     })
            {
                File.WriteAllText(_repository.Paths.ToAbsolute(relative), "synthetic");
            }
        }

        return await _repository.SaveAsync(new SaveItemInput(
            new AssetInput(
                hash,
                $"originals/{hash}.png",
                $"thumbnails/small/{hash}.jpg",
                $"thumbnails/medium/{hash}.jpg",
                1600,
                900,
                "png"),
            $"prompt {hash}",
            "",
            null,
            []));
    }

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection =
            new SqliteConnection($"Data Source={_repository.Paths.Database};Pooling=False");
        await connection.OpenAsync();
        var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        await foreignKeys.ExecuteNonQueryAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
}
