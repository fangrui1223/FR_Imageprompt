using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PromptVault.App;
using PromptVault.Core;

internal static class BoardHistoryProbe
{
    internal static void Run(string outputRoot)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(BoardWindow);
        // The history method only needs two stacks; a null UndoButton skips UI updates.
        // No window constructor, application startup, clipboard, or repository is used.
        var window = RuntimeHelpers.GetUninitializedObject(type);
        var sceneType = type.GetNestedType("BoardSceneSnapshot", BindingFlags.NonPublic)!;
        var stackType = typeof(Stack<>).MakeGenericType(sceneType);
        var undo = Activator.CreateInstance(stackType)!;
        type.GetField("_undo", flags)!.SetValue(window, undo);
        type.GetField("_redo", flags)!.SetValue(window, Activator.CreateInstance(stackType));
        var commit = type.GetMethod("CommitSceneHistorySnapshot", flags)!;
        var snapshots = new List<object>();
        var samples = new List<object>();
        for (var step = 1; step <= 52; step++)
        {
            var snapshot = Activator.CreateInstance(sceneType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { Array.Empty<BoardItemRecord>(), Array.Empty<BoardNoteRecord>() }, null)!;
            snapshots.Add(snapshot);
            commit.Invoke(window, new[] { snapshot });
            if (step >= 50)
            {
                var retained = ((IEnumerable)undo).Cast<object>()
                    .Select(item => snapshots.FindIndex(original => ReferenceEquals(original, item)) + 1)
                    .ToArray();
                samples.Add(new { commits = step, expectedNextUndoSnapshot = step,
                    actualNextUndoSnapshot = retained[0], retainedSnapshotsNewestFirst = retained,
                    bugReproduced = retained[0] != step });
            }
        }
        var result = JsonSerializer.Serialize(new
        {
            entryPoint = "BoardWindow.CommitSceneHistorySnapshot (production method via reflection)",
            fixture = "In-memory history stacks; no window initialization or user data access",
            samples
        }, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(outputRoot);
        File.WriteAllText(Path.Combine(outputRoot, "board-history-reproduction.json"), result);
        Console.WriteLine(result);
    }
}
