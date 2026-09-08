using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardReferenceLayoutTests
{
    [Theory]
    [InlineData(BoardImageLayoutKind.Left)]
    [InlineData(BoardImageLayoutKind.Center)]
    [InlineData(BoardImageLayoutKind.Right)]
    [InlineData(BoardImageLayoutKind.Top)]
    [InlineData(BoardImageLayoutKind.Middle)]
    [InlineData(BoardImageLayoutKind.Bottom)]
    public void AlignUsesRotatedFramesAndPreservesAllOtherData(BoardImageLayoutKind kind)
    {
        var source = new[] { Item(1, -120, 80, 100, 160, 31), Item(2, 160, -60, 80, 110, -13), Item(3, 430, 225, 170, 90, 90) };
        var arranged = BoardImageLayout.Arrange(source, kind);
        var bounds = arranged.Select(BoardCameraEngine.ItemBounds).ToArray();
        double Edge(BoardWorldRect b) => kind switch
        {
            BoardImageLayoutKind.Left => b.X,
            BoardImageLayoutKind.Center => b.X + b.Width / 2,
            BoardImageLayoutKind.Right => b.X + b.Width,
            BoardImageLayoutKind.Top => b.Y,
            BoardImageLayoutKind.Middle => b.Y + b.Height / 2,
            _ => b.Y + b.Height
        };
        Assert.All(bounds, b => Assert.Equal(Edge(bounds[0]), Edge(b), 8));
        for (var i = 0; i < source.Length; i++)
            Assert.Equal(source[i], arranged[i] with { X = source[i].X, Y = source[i].Y });
        var repeated = BoardImageLayout.Arrange(arranged, kind);
        for (var i = 0; i < arranged.Count; i++)
        {
            Assert.Equal(arranged[i].X, repeated[i].X, 8);
            Assert.Equal(arranged[i].Y, repeated[i].Y, 8);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EqualSpacingKeepsBothAnchorsAndAccountsForDifferentRotatedSizes(bool horizontal)
    {
        var source = new[] { Item(1, 0, 0, 100, 100, 25), Item(2, 180, 195, 170, 90, -10), Item(3, 660, 740, 130, 180, 90) };
        var arranged = BoardImageLayout.Arrange(source, horizontal ? BoardImageLayoutKind.HorizontalSpacing : BoardImageLayoutKind.VerticalSpacing);
        Assert.Equal(source[0], arranged[0]);
        Assert.Equal(source[2], arranged[2]);
        var b = arranged.Select(BoardCameraEngine.ItemBounds).ToArray();
        var firstGap = horizontal ? b[1].X - b[0].X - b[0].Width : b[1].Y - b[0].Y - b[0].Height;
        var secondGap = horizontal ? b[2].X - b[1].X - b[1].Width : b[2].Y - b[1].Y - b[1].Height;
        Assert.Equal(firstGap, secondGap, 8);
        Assert.True(firstGap >= 0);
        Assert.Equal(source[1], arranged[1] with { X = source[1].X, Y = source[1].Y });
        var reordered = BoardImageLayout.Arrange(source.Reverse().ToArray(), horizontal ? BoardImageLayoutKind.HorizontalSpacing : BoardImageLayoutKind.VerticalSpacing);
        Assert.Equal(arranged.OrderBy(i => i.Id), reordered.OrderBy(i => i.Id));
    }

    [Fact]
    public void DistributionRefusesInsufficientSpaceWithoutChangingInputs()
    {
        var source = new[] { Item(1, 0, 0), Item(2, 10, 10), Item(3, 20, 20) };
        var before = source.ToArray();
        Assert.Throws<InvalidOperationException>(() => BoardImageLayout.Arrange(source, BoardImageLayoutKind.HorizontalSpacing));
        Assert.Equal(before, source);
        Assert.Equal(source.Take(2), BoardImageLayout.Arrange(source.Take(2).ToArray(), BoardImageLayoutKind.HorizontalSpacing));
    }

    [Fact]
    public void ReferenceLockBlocksEveryEditingCommandButKeepsNavigation()
    {
        var state = new BoardCommandState(BoardCommandContextKind.Item, 3, 2, true, true, true, 3, false, true);
        var allowed = new HashSet<BoardCommandId>
        {
            BoardCommandId.FocusSelection, BoardCommandId.FocusAll, BoardCommandId.ResetView,
            BoardCommandId.SelectAll, BoardCommandId.Copy, BoardCommandId.OpenOriginal,
            BoardCommandId.NewBoard, BoardCommandId.ShowInspector, BoardCommandId.ToggleTopmost,
            BoardCommandId.ShowTopBar, BoardCommandId.ShowSettings, BoardCommandId.ShowShortcuts, BoardCommandId.ToggleReferenceLock
        };
        foreach (var command in Enum.GetValues<BoardCommandId>().Where(id => !allowed.Contains(id)))
            Assert.False(BoardCommandPolicy.CanExecute(command, state), command.ToString());
        foreach (var command in allowed)
            Assert.True(BoardCommandPolicy.CanExecute(command, state with { SelectedItemCount = 1 }), command.ToString());
    }

    [Fact]
    public void LayoutAvailabilityRequiresEnoughPicturesRegardlessOfNotes()
    {
        var state = new BoardCommandState(BoardCommandContextKind.Note, 1, 50, false, false, false, 1, false);
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.AlignImagesLeft, state));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.AlignImagesLeft, state with { SelectedItemCount = 2 }));
        Assert.False(BoardCommandPolicy.CanExecute(BoardCommandId.DistributeImagesHorizontally, state with { SelectedItemCount = 2 }));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.DistributeImagesHorizontally, state with { SelectedItemCount = 3 }));
        Assert.True(BoardCommandPolicy.CanExecute(BoardCommandId.RemoveSelection, state with { SelectedItemCount = 0 }));
    }

    [Fact]
    public void ReferencePreferenceRoundTripsAndSeparatesLibrariesAndBoards()
    {
        var root = Path.Combine(Path.GetTempPath(), "PromptVaultReferenceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "settings.json");
            File.WriteAllText(file, "{\"ReferenceLockedBoards\":null}");
            var settings = AppSettings.Load(file);
            Assert.Empty(settings.ReferenceLockedBoards);
            var key = AppSettings.BoardReferenceKey(root, 1);
            settings.ReferenceLockedBoards.Add(key);
            settings.Save();
            var loaded = AppSettings.Load(file);
            Assert.Contains(AppSettings.BoardReferenceKey(root.ToUpperInvariant() + Path.DirectorySeparatorChar, 1), loaded.ReferenceLockedBoards);
            Assert.DoesNotContain(AppSettings.BoardReferenceKey(root, 2), loaded.ReferenceLockedBoards);
            Assert.DoesNotContain(AppSettings.BoardReferenceKey(Path.Combine(root, "another"), 1), loaded.ReferenceLockedBoards);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BoardItemRecord Item(long id, double x, double y, double width = 100, double height = 100, double rotation = 0)
    {
        var now = DateTimeOffset.UtcNow;
        return new BoardItemRecord(id, 1, id, $"{id}.png", null, null, null, null,
            100, 100, "PNG", x, y, width, height, 4, rotation, .1, .05, .2, .15, 7, now, now);
    }
}
