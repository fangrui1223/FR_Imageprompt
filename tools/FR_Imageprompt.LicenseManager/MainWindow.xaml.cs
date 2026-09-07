using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FR_Imageprompt.LicenseManager;

public partial class MainWindow : Window
{
    private readonly LicenseAuthorityService _authority;

    internal MainWindow(LicenseAuthorityService authority)
    {
        _authority = authority;
        InitializeComponent();
        RefreshAuthorityStatus();
        ResultText.Text = "填写使用人和收件人发来的设备请求码，然后生成许可证文件。";
    }

    private void GenerateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var oneYear = (DurationBox.SelectedItem as ComboBoxItem)?.Tag as string == "OneYear";
            var document = _authority.Issue(LicenseeBox.Text, DeviceCodeBox.Text, oneYear, DateTimeOffset.UtcNow);
            var safeName = string.Concat(LicenseeBox.Text.Trim().Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "device";
            var dialog = new SaveFileDialog
            {
                Title = "保存 FR_Imageprompt 许可证",
                Filter = "FR_Imageprompt 许可证 (*.frlicense)|*.frlicense",
                FileName = $"FR_Imageprompt-{safeName}.frlicense",
                AddExtension = true,
                DefaultExt = ".frlicense"
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, document, new System.Text.UTF8Encoding(false));
            ResultText.Text = $"许可证已生成：{dialog.FileName}\n期限：{(oneYear ? "一年" : "永久")}\n该文件只在指定设备请求码对应的电脑上有效。";
        }
        catch (Exception exception)
        {
            ResultText.Text = exception.Message;
        }
    }

    private void BackupClick(object sender, RoutedEventArgs e)
    {
        var password = PasswordDialog.Request(this, "备份签发密钥", "设置至少 10 个字符的备份口令：", confirm: true);
        if (password is null) return;
        try
        {
            var document = _authority.ExportEncryptedBackup(password);
            var dialog = new SaveFileDialog
            {
                Title = "保存加密签发密钥备份",
                Filter = "FR_Imageprompt 签发密钥备份 (*.frauthority)|*.frauthority",
                FileName = $"FR_Imageprompt-V2-authority-{DateTime.Now:yyyyMMdd}.frauthority",
                AddExtension = true,
                DefaultExt = ".frauthority"
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, document, new System.Text.UTF8Encoding(false));
            ResultText.Text = "加密签发密钥备份已保存。请把备份文件与口令分开保管。";
        }
        catch (Exception exception) { ResultText.Text = exception.Message; }
    }

    private void RestoreClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "恢复 V2 签发密钥",
            Filter = "FR_Imageprompt 签发密钥备份 (*.frauthority)|*.frauthority",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var password = PasswordDialog.Request(this, "恢复签发密钥", "输入备份口令：", confirm: false);
        if (password is null) return;
        try
        {
            _authority.RestoreEncryptedBackup(File.ReadAllText(dialog.FileName), password);
            RefreshAuthorityStatus();
            ResultText.Text = "V2 签发密钥已恢复到 Windows 凭据管理器。";
        }
        catch (Exception exception) { ResultText.Text = exception.Message; }
    }

    private void RefreshAuthorityStatus()
    {
        try
        {
            AuthorityStatusText.Text = !_authority.HasPrivateKey
                ? "未找到 V2 签发私钥。请从加密备份恢复。"
                : _authority.PublicKeyMatchesProduct()
                    ? "V2 签发私钥已就绪，与当前 FR_Imageprompt 公钥匹配。"
                    : "签发私钥与当前 FR_Imageprompt V2 公钥不匹配，签发已禁用。";
        }
        catch (Exception exception) { AuthorityStatusText.Text = exception.Message; }
    }
}
