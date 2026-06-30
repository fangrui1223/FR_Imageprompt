using System.Windows;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class App : System.Windows.Application
{
    private TrayService? _tray;
    private AppSettings? _settings;
    private LibraryRepository? _repository;
    private CaptureCoordinator? _capture;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _settings = AppSettings.Load();
            if (string.IsNullOrWhiteSpace(_settings.LibraryRoot) || !Directory.Exists(_settings.LibraryRoot))
            {
                var setup = new LibrarySetupWindow();
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }

                _settings.LibraryRoot = setup.LibraryRoot;
                _settings.Save();
            }

            var paths = new LibraryPaths(_settings.LibraryRoot);
            _repository = new LibraryRepository(paths);
            await _repository.InitializeAsync();
            _capture = new CaptureCoordinator(_repository);
            var window = CreateMainWindow(false, null);
            MainWindow = window;
            _tray = new TrayService(window, () => Shutdown());
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"FR_Imageprompt 无法启动：\n{ex.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    internal void SwitchMainWindow(bool transparent, MainWindowSnapshot snapshot)
    {
        if (_repository is null || _capture is null || _settings is null) return;
        var oldWindow = MainWindow as MainWindow;
        var next = CreateMainWindow(transparent, snapshot);
        MainWindow = next;
        _tray?.UpdateWindow(next);
        next.Show();
        next.Activate();
        if (oldWindow is not null)
        {
            oldWindow.AllowClose();
            oldWindow.Close();
        }
    }

    private MainWindow CreateMainWindow(bool transparent, MainWindowSnapshot? snapshot)
    {
        if (_repository is null || _capture is null || _settings is null) throw new InvalidOperationException("FR_Imageprompt 尚未完成初始化。");
        return new MainWindow(_repository, _capture, _settings, transparent, snapshot);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}