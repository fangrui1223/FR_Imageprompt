using System.Windows;
using System.Windows.Media;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class AiSettingsWindow : Window
{
    private readonly AppSettings _settings;

    public AiSettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        OnlineEnabledCheckBox.IsChecked = settings.OnlineAiEnabled;
        EndpointBox.Text = settings.OnlineAiEndpoint;
        ModelBox.Text = settings.OnlineAiModel;
        IncludePromptCheckBox.IsChecked = settings.OnlineAiIncludeExistingPrompt;
        KeyStateText.Text = WindowsCredentialStore.HasOnlineAiKey()
            ? "Windows 凭据管理器中已有密钥；留空会保留原值。"
            : "尚未保存密钥。输入后将安全保存到 Windows 凭据管理器。";
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var enabled = OnlineEnabledCheckBox.IsChecked == true;
        var candidate = new AppSettings
        {
            OnlineAiEnabled = enabled,
            OnlineAiEndpoint = EndpointBox.Text.Trim(),
            OnlineAiModel = ModelBox.Text.Trim(),
            OnlineAiIncludeExistingPrompt = IncludePromptCheckBox.IsChecked == true
        };
        if (enabled
            && !OnlineAiConfiguration.TryCreate(candidate, out _, out var validationError))
        {
            ShowError(validationError ?? "在线 AI 设置不完整。");
            return;
        }
        if (enabled
            && string.IsNullOrWhiteSpace(ApiKeyBox.Password)
            && !WindowsCredentialStore.HasOnlineAiKey())
        {
            ShowError("启用在线 AI 前，请输入 API 密钥。");
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
                WindowsCredentialStore.SaveOnlineAiKey(ApiKeyBox.Password);
            _settings.OnlineAiEnabled = enabled;
            _settings.OnlineAiEndpoint = candidate.OnlineAiEndpoint;
            _settings.OnlineAiModel = candidate.OnlineAiModel;
            _settings.OnlineAiIncludeExistingPrompt = candidate.OnlineAiIncludeExistingPrompt;
            _settings.Save();
            ApiKeyBox.Clear();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("online-ai-settings", "Online AI settings could not be saved.", ex);
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        StatusText.Foreground = Brushes.IndianRed;
        StatusText.Text = message;
    }
}
