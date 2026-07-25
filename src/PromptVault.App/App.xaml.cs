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
    private ExternalFolderIndexService? _externalIndex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionLogging();
        AppLog.Information("startup", "Application startup began.");
        DevelopmentPerformanceTrace.Event("app-startup-begin");
        var isAiWorker = e.Args.Contains("--ai-worker", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (isAiWorker)
            {
                var libraryRoot = GetOptionValue(e.Args, "--library")
                    ?? throw new ArgumentException("--ai-worker 需要 --library 参数。");
                var workerSettings = AppSettings.Load(GetOptionValue(e.Args, "--settings"));
                var processed = await AiWorkerHost.RunOnceAsync(libraryRoot, workerSettings);
                AppLog.Information("ai-worker", "Independent AI worker completed.", new { processed });
                Shutdown(processed >= 0 ? 0 : 1);
                return;
            }
            _settings = AppSettings.Load(GetOptionValue(e.Args, "--settings"));
            VisualModeService.Apply(transparent: false, _settings.ReducedMotionEnabled);
            if (!string.IsNullOrWhiteSpace(_settings.RecoveryNotice))
            {
                MessageBox.Show(
                    _settings.RecoveryNotice,
                    "设置已恢复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
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
            _repository.Diagnostic += (_, diagnostic) =>
                AppLog.Warning(diagnostic.Area, diagnostic.Message, diagnostic.Exception);
            using (DevelopmentPerformanceTrace.Measure("repository-initialize"))
            {
                await _repository.InitializeAsync();
            }
            if (_repository.LastMigration is { WasUpgraded: true } migration)
            {
                AppLog.Information("database-migration", "Database migration completed.", new
                {
                    migration.FromVersion,
                    migration.ToVersion,
                    migration.BackupPath
                });
            }
            _capture = new CaptureCoordinator(_repository);
            _externalIndex = new ExternalFolderIndexService(_repository);
            _externalIndex.Start(_settings.ExternalFolders);
            var window = CreateMainWindow(false, null);
            MainWindow = window;
            _tray = new TrayService(window, () => Shutdown());
            window.Show();
            DevelopmentPerformanceTrace.Event("main-window-shown");
        }
        catch (Exception ex)
        {
            AppLog.Error("startup", ex);
            if (isAiWorker)
            {
                Shutdown(-1);
                return;
            }
            MessageBox.Show(
                $"FR_Imageprompt 无法启动：\n{ex.Message}\n\n诊断日志：{AppLog.CurrentLogPath}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static string? GetOptionValue(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"{option} 需要一个设置文件路径。");
            }
            return Path.GetFullPath(args[index + 1]);
        }
        return null;
    }

    private void RegisterGlobalExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("dispatcher-unhandled", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLog.Error("appdomain-unhandled", exception, data: new { args.IsTerminating });
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("task-unobserved", args.Exception);
            args.SetObserved();
        };
    }

    internal void SwitchMainWindow(bool transparent, MainWindowSnapshot snapshot)
    {
        if (_repository is null || _capture is null || _settings is null || _externalIndex is null) return;
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
        if (_repository is null || _capture is null || _settings is null || _externalIndex is null) throw new InvalidOperationException("FR_Imageprompt 尚未完成初始化。");
        return new MainWindow(_repository, _capture, _settings, _externalIndex, transparent, snapshot);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Information("shutdown", "Application exit.", new { e.ApplicationExitCode });
        _externalIndex?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
