using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using PromptVault.App;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardHistoryRetentionTests
{
    [Theory]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(52)]
    [InlineData(100)]
    public void ProductionHistoryRetainsNewestSnapshotsInOrder(int count)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(BoardWindow);
        var window = RuntimeHelpers.GetUninitializedObject(type);
        var sceneType = type.GetNestedType("BoardSceneSnapshot", BindingFlags.NonPublic)!;
        var stackType = typeof(Stack<>).MakeGenericType(sceneType);
        var undo = Activator.CreateInstance(stackType)!;
        var redo = Activator.CreateInstance(stackType)!;
        type.GetField("_undo", flags)!.SetValue(window, undo);
        type.GetField("_redo", flags)!.SetValue(window, redo);
        var commit = type.GetMethod("CommitSceneHistorySnapshot", flags)!;
        var snapshots = new List<object>();
        for (var step = 0; step < count; step++)
        {
            var snapshot = Activator.CreateInstance(sceneType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { Array.Empty<BoardItemRecord>(), Array.Empty<BoardNoteRecord>() }, null)!;
            snapshots.Add(snapshot);
            stackType.GetMethod("Push")!.Invoke(redo, [snapshot]);
            commit.Invoke(window, [snapshot]);
            Assert.Empty((IEnumerable)redo);
        }
        var actual = ((IEnumerable)undo).Cast<object>().ToArray();
        var expected = snapshots.AsEnumerable().Reverse().Take(50).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++) Assert.Same(expected[index], actual[index]);
    }
}
