using System.Windows;
using PromptVault.App.Services;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace PromptVault.App;

public partial class MainWindow
{
    private async void ExportDiagnosticsClick(object sender, RoutedEventArgs e) =>
        await ExportDiagnosticsAsync();

    private async Task ExportDiagnosticsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 FR_Imageprompt 诊断包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            FileName = $"FR_Imageprompt-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var result = await DiagnosticBundleExporter.ExportAsync(dialog.FileName, _repository.Paths);
            AppLog.Information("diagnostics-export", "A redacted diagnostic bundle was exported.", new
            {
                result.SizeBytes,
                result.LogFileCount,
                result.Entries
            });
            MessageBox.Show(
                this,
                $"诊断包已导出：\n{result.ZipPath}\n\n"
                + "内容仅包括脱敏日志、应用版本和白名单环境信息；"
                + "不包含用户图片、提示词、在线 AI 密钥或设置文件。",
                "诊断包已导出",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Warning("diagnostics-export", "The diagnostic bundle could not be exported.", ex);
            MessageBox.Show(
                this,
                $"诊断包导出失败：\n{ex.Message}",
                "导出失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
