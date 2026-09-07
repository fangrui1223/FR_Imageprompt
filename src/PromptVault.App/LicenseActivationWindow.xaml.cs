using System.Windows;
using PromptVault.App.Services;
using PromptVault.Licensing;

namespace PromptVault.App;

public partial class LicenseActivationWindow : Window
{
    private readonly ProductLicenseService _licenseService;

    internal LicenseActivationWindow(ProductLicenseService licenseService, LicenseValidationResult validation)
    {
        _licenseService = licenseService;
        InitializeComponent();
        DeviceCodeBox.Text = licenseService.DeviceCode;
        ShowStatus(validation.Message, validation.IsValid);
    }

    internal string DeviceCodeText => DeviceCodeBox.Text;
    internal string StatusMessage => StatusText.Text;

    private void CopyDeviceCodeClick(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_licenseService.DeviceCode);
        ShowStatus("设备请求码已复制，可以发送给授权人。", true);
    }

    private void ImportLicenseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 FR_Imageprompt 许可证",
            Filter = "FR_Imageprompt 许可证 (*.frlicense)|*.frlicense|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var validation = _licenseService.Import(dialog.FileName);
        ShowStatus(validation.Message, validation.IsValid);
        if (!validation.IsValid) return;
        DialogResult = true;
    }

    private void ExitClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowStatus(string message, bool success)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            success ? "AccentBrush" : "DangerBrush");
    }
}
