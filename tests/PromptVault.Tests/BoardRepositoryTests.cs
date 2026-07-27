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
