using System.Text.Json;

namespace PromptVault.App;

public partial class App
{
    private async Task<bool> RunM111BoardSmokeAsync(string reportPath)
    {
        var results = new Dictionary<string, bool>();
        string? error = null;
        var path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (_settings is null || _boardWorkspace is null) throw new InvalidOperationException("App is not ready.");
            var board = await _boardWorkspace.OpenAsync(MainWindow);
            await board.RunM111SmokeAsync(path, _settings, results);
            if (!await board.PrepareForApplicationExitAsync()) throw new InvalidOperationException("Pending saves remain.");
            board.ApproveApplicationExit();
            board.Close();
        }
        catch (Exception ex) { error = ex.ToString(); }
        var passed = error is null && results.Count >= 30 && results.Values.All(value => value);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Milestone = "M11.1", GeneratedAt = DateTimeOffset.Now, DataKind = "synthetic",
            Passed = passed, Results = results, Error = error
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
}
