using System.Text.Json;
using System.Windows.Threading;

namespace PromptVault.App;

public partial class App
{
    private async Task<bool> RunM11StabilitySmokeAsync(string reportPath)
    {
        var results = new Dictionary<string, bool>();
        string? error = null;
        try
        {
            if (_settings is null || _boardWorkspace is null || MainWindow is not MainWindow source)
                throw new InvalidOperationException("M11 app is not ready.");
            var snapshot = await source.PrepareM102HandoffProbeAsync(_settings, 0.5);
            var first = SwitchMainWindowAsync(source, true);
            results["prewarmWheelSelectionAndCloseIgnored"] = source.VerifyM11FrozenInput();
            var duplicate = await SwitchMainWindowAsync(source, true);
            results["duplicateRejectedWithoutCancelingTransfer"] = !duplicate && await first;
            var next = (MainWindow)MainWindow;
            results["duplicateHandoffRowsAndPositionRetained"] = (await next.ValidateM102HandoffAsync(_settings, snapshot, _lastMainWindowHandoff)).Passed;
            var recoverySnapshot = await next.PrepareM102HandoffProbeAsync(_settings, 0.2);
            var failing = SwitchMainWindowAsync(next, false);
            var staged = Windows.OfType<MainWindow>().FirstOrDefault(window => !ReferenceEquals(window, next));
            staged?.AbandonStagedWindow();
            results["abortedPreparationRestoresSource"] = !await failing && ReferenceEquals(MainWindow, next) && next.IsVisible;
            results["rollbackRowsRemainIntact"] = next.Rows.Count > 0 && next.Rows.All(row => row.Items.Count > 0);
            results["retryAfterAbortedPreparation"] = await SwitchMainWindowAsync(next, false);
            results["rollbackRetryPositionRetained"] = (await ((MainWindow)MainWindow).ValidateM102HandoffAsync(_settings, recoverySnapshot, _lastMainWindowHandoff)).Passed;
            results["mouseCaptureLossEndsWindowDrag"] = ((MainWindow)MainWindow).VerifyM11CaptureInterruption();
            var board = await _boardWorkspace.OpenAsync(MainWindow);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (var pair in await board.RunM11PersistenceSmokeAsync(_settings)) results[pair.Key] = pair.Value;
            results["allPendingSavedBeforeExit"] = await board.PrepareForApplicationExitAsync();
            board.ApproveApplicationExit();
            board.Close();
        }
        catch (Exception ex) { error = ex.ToString(); }
        var passed = error is null && results.Count == 21 && results.Values.All(value => value);
        var path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Milestone = "M11", DataKind = "synthetic", Passed = passed, Results = results, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
}
