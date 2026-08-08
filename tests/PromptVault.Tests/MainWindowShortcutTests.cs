using System.Windows.Input;
using PromptVault.App;

namespace PromptVault.Tests;

public sealed class MainWindowShortcutTests
{
    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, true)]
    [InlineData(ModifierKeys.Control, false)]
    [InlineData(ModifierKeys.Shift, false)]
    [InlineData(ModifierKeys.None, false)]
    public void CtrlShiftFOpensBoardBeforeSearch(ModifierKeys modifiers, bool expected)
    {
        Assert.Equal(expected, MainWindow.IsOpenBoardShortcut(Key.F, modifiers));
    }

    [Fact]
    public void CtrlShiftFOpensBoardAfterImeResolvesToF()
    {
        var resolved = MainWindow.ResolveShortcutKey(Key.ImeProcessed, Key.F, Key.None);

        Assert.True(MainWindow.IsOpenBoardShortcut(
            resolved,
            ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void ShortcutResolverAcceptsSystemKeys()
    {
        Assert.Equal(Key.F, MainWindow.ResolveShortcutKey(Key.System, Key.None, Key.F));
    }
}
