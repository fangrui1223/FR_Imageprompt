using System.Text.Json;

namespace PromptVault.App;

public partial class App
{
    private async Task<bool> RunM112SmokeAsync(MainWindow window, string reportPath)
    {
        object? startup = null;
        var closes = new List<Dictionary<string, bool>>();
        string? error = null;
        try
        {
            startup = await window.FinishM112StartupSamplingAsync();
            for (var cycle = 0; cycle < 4; cycle++)
            {
                var board = await _boardWorkspace!.OpenAsync(window);
                closes.Add(await board.RunM112CloseSmokeAsync(cycle % 2 != 0));
                window.Activate();
                window.Focus();
                await Task.Delay(650);
                closes[^1]["MainWindowRemainsUsable"] = window.IsVisible && window.IsEnabled;
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        var startupJson = JsonSerializer.SerializeToElement(startup);
        var passed = error is null && startupJson.GetProperty("Passed").GetBoolean()
            && startupJson.GetProperty("AllMetadataLoaded").GetBoolean()
            && closes.Count == 4 && closes.All(cycle => cycle.Values.All(value => value));
        var path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Milestone = "M11.2", GeneratedAt = DateTimeOffset.Now, DataKind = "synthetic",
            Passed = passed, Startup = startup, CloseCycles = closes, Error = error
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
}
