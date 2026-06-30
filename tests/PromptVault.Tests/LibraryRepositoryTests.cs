using PromptVault.Core;

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
        var results = await _repository.SearchAsync(new SearchOptions(Query: "city night"));
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
        var results = await _repository.SearchAsync(new SearchOptions(Query: "updated"));
        Assert.Single(results);
        Assert.Equal("new note", results[0].Notes);
    }

    [Fact]
    public async Task TrashCanBeRestored()
    {
        var saved = await SaveAsync("trash-hash", "trash me", "", []);
        await _repository.MoveToTrashAsync(saved.ItemId);
        Assert.Empty(await _repository.SearchAsync(new SearchOptions()));
        Assert.Single(await _repository.SearchAsync(new SearchOptions(IncludeTrash: true)));
        await _repository.RestoreAsync(saved.ItemId);
        Assert.Single(await _repository.SearchAsync(new SearchOptions()));
    }

    [Fact]
    public async Task DeletingCategoryMovesItemToUnclassified()
    {
        var categoryId = await _repository.AddCategoryAsync("test-category", "test");
        await SaveAsync("category-hash", "category", "", [], categoryId);
        await _repository.DeleteCategoryAsync(categoryId);
        var item = Assert.Single(await _repository.SearchAsync(new SearchOptions()));
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

        var results = await _repository.SearchAsync(new SearchOptions(CategoryId: categoryId));
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

        var partial = await _repository.SearchAsync(new SearchOptions(Tag: "pant"));
        Assert.Single(partial);
        Assert.Equal("pants prompt", partial[0].Prompt);

        var multiple = await _repository.SearchAsync(new SearchOptions(Tag: "pant, blue"));
        Assert.Single(multiple);
        Assert.Equal("pants prompt", multiple[0].Prompt);
    }

    [Fact]
    public async Task CanUpdateTagsAndNotesForMultipleItems()
    {
        var first = await SaveAsync("metadata-a", "metadata a", "old", ["old"]);
        var second = await SaveAsync("metadata-b", "metadata b", "old", ["old"]);

        await _repository.UpdateItemsMetadataAsync([first.ItemId, second.ItemId], "batch, edited", "shared note");

        var results = await _repository.SearchAsync(new SearchOptions(Tag: "edit"));
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
    private Task<SaveResult> SaveAsync(string hash, string prompt, string notes, IReadOnlyList<string> tags, long? category = null) =>
        _repository.SaveAsync(new SaveItemInput(
            new AssetInput(hash, $"originals/{hash}.png", $"thumbnails/small/{hash}.jpg", $"thumbnails/medium/{hash}.jpg", 100, 100, "png"),
            prompt, notes, category, tags));

    private void SqliteCleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
