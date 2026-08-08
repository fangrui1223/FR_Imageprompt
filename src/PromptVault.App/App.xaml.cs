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
    private BoardWorkspaceService? _boardWorkspace;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionLogging();
        AppLog.Information("startup", "Application startup began.");
        DevelopmentPerformanceTrace.Event("app-startup-begin");
        var isAiWorker = e.Args.Contains("--ai-worker", StringComparer.OrdinalIgnoreCase);
        var openBoardForDiagnostics = e.Args.Contains(
            "--open-board",
            StringComparer.OrdinalIgnoreCase);
        string? boardCameraSmokeReport = null;
        string? boardInputSmokeReport = null;
        string? boardCommandSmokeReport = null;
        string? boardChromeSmokeReport = null;
        string? boardInspectorSmokeReport = null;
        string? boardTransformSmokeReport = null;
        string? boardImageSmokeReport = null;
        string? boardM8GateReport = null;
        string? boardM82SmokeReport = null;
        string? boardM83SmokeReport = null;
        string? boardM84SmokeReport = null;
        string? galleryPartialCardSmokeReport = null;
        try
        {
            boardCameraSmokeReport = GetOptionValue(e.Args, "--board-camera-smoke");
            boardInputSmokeReport = GetOptionValue(e.Args, "--board-input-smoke");
            boardCommandSmokeReport = GetOptionValue(e.Args, "--board-command-smoke");
            boardChromeSmokeReport = GetOptionValue(e.Args, "--board-chrome-smoke");
            boardInspectorSmokeReport = GetOptionValue(e.Args, "--board-inspector-smoke");
            boardTransformSmokeReport = GetOptionValue(e.Args, "--board-transform-smoke");
            boardImageSmokeReport = GetOptionValue(e.Args, "--board-image-smoke");
            boardM8GateReport = GetOptionValue(e.Args, "--board-m8-gate");
            boardM82SmokeReport = GetOptionValue(e.Args, "--board-m82-smoke");
            boardM83SmokeReport = GetOptionValue(e.Args, "--board-m83-smoke");
            boardM84SmokeReport = GetOptionValue(e.Args, "--board-m84-smoke");
            galleryPartialCardSmokeReport = GetOptionValue(e.Args, "--gallery-partial-card-smoke");
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
                await _repository.InitializeAsync(new LibraryUpgradeOptions(_settings.StorageFilePath));
            }
            if (_repository.LastUpgradeRecovery is { Recovered: true } recovery)
            {
                AppLog.Warning("database-recovery", recovery.Message, data: new
                {
                    recovery.ResumedValidation,
                    recovery.SessionDirectory,
                    recovery.DatabaseBackupPath,
                    recovery.PreservedOriginalFileCount
                });
                MessageBox.Show(
                    $"{recovery.Message}\n\n恢复记录：{recovery.SessionDirectory}",
                    "图库升级恢复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            if (_repository.LastMigration is { WasUpgraded: true } migration)
            {
                AppLog.Information("database-migration", "Database migration completed.", new
                {
                    migration.FromVersion,
                    migration.ToVersion,
                    migration.BackupPath
                });
                var report = _repository.LastUpgrade;
                MessageBox.Show(
                    $"图库已从版本 {migration.FromVersion} 升级到版本 {migration.ToVersion}。\n\n"
                    + $"可恢复备份：{report?.SessionDirectory ?? migration.BackupPath}\n"
                    + "升级备份只包含数据库与设置；原图和缩略图不会被回滚覆盖。",
                    "图库升级完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            var recoveredAiJobs = await _repository.RecoverInterruptedAiJobsAsync();
            if (recoveredAiJobs > 0)
            {
                AppLog.Warning(
                    "ai-job-recovery",
                    "Interrupted AI jobs were returned to the durable queue.",
                    data: new { recoveredAiJobs });
            }
            _capture = new CaptureCoordinator(_repository);
            _boardWorkspace = new BoardWorkspaceService(_repository, _settings);
            _externalIndex = new ExternalFolderIndexService(_repository);
            _externalIndex.Start(_settings.ExternalFolders);
            var window = CreateMainWindow(false, null);
            MainWindow = window;
            _tray = new TrayService(window, () => Shutdown());
            window.Show();
            if (galleryPartialCardSmokeReport is not null)
            {
                var passed = false;
                try
                {
                    passed = await window.RunPartialCardInputSmokeAsync(
                        galleryPartialCardSmokeReport,
                        _settings);
                }
                catch (Exception diagnosticException)
                {
                    var reportPath = Path.GetFullPath(galleryPartialCardSmokeReport);
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                    await File.WriteAllTextAsync(
                        reportPath,
                        System.Text.Json.JsonSerializer.Serialize(
                            new
                            {
                                Milestone = "M7.2.2-partial-card-first-click",
                                GeneratedAt = DateTimeOffset.Now,
                                DataKind = "synthetic",
                                Passed = false,
                                Error = diagnosticException.ToString()
                            },
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                Shutdown(passed ? 0 : 2);
                return;
            }
            if (openBoardForDiagnostics || boardCameraSmokeReport is not null || boardInputSmokeReport is not null || boardCommandSmokeReport is not null || boardChromeSmokeReport is not null || boardInspectorSmokeReport is not null || boardTransformSmokeReport is not null || boardImageSmokeReport is not null || boardM8GateReport is not null || boardM82SmokeReport is not null || boardM83SmokeReport is not null || boardM84SmokeReport is not null)
            {
                var boardWindow = await _boardWorkspace.OpenAsync(window);
                if (boardCameraSmokeReport is not null)
                {
                    await boardWindow.RunCameraSmokeAsync(boardCameraSmokeReport, _settings);
                }
                if (boardInputSmokeReport is not null)
                {
                    await boardWindow.RunInputSmokeAsync(boardInputSmokeReport, _settings);
                }
                if (boardCommandSmokeReport is not null)
                {
                    await boardWindow.RunCommandSmokeAsync(boardCommandSmokeReport, _settings);
                }
                if (boardChromeSmokeReport is not null)
                {
                    await boardWindow.RunChromeSmokeAsync(boardChromeSmokeReport, _settings);
                }
                if (boardInspectorSmokeReport is not null)
                {
                    await boardWindow.RunInspectorSmokeAsync(boardInspectorSmokeReport, _settings);
                }
                if (boardTransformSmokeReport is not null)
                {
                    await boardWindow.RunTransformSmokeAsync(boardTransformSmokeReport, _settings);
                }
                if (boardImageSmokeReport is not null)
                {
                    await boardWindow.RunImageSmokeAsync(boardImageSmokeReport, _settings);
                }
                if (boardM8GateReport is not null)
                {
                    await boardWindow.RunM8GateSmokeAsync(boardM8GateReport, _settings);
                }
                if (boardM82SmokeReport is not null)
                {
                    await boardWindow.RunM82SmokeAsync(boardM82SmokeReport, _settings);
                }
                if (boardM83SmokeReport is not null)
                {
                    await boardWindow.RunM83SmokeAsync(boardM83SmokeReport, _settings);
                }
                if (boardM84SmokeReport is not null)
                {
                    await boardWindow.RunM84SmokeAsync(boardM84SmokeReport, _settings);
                }
            }
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
                throw new ArgumentException($"{option} 需要一个路径参数。");
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
        if (_repository is null || _capture is null || _settings is null || _externalIndex is null || _boardWorkspace is null) throw new InvalidOperationException("FR_Imageprompt 尚未完成初始化。");
        return new MainWindow(_repository, _capture, _settings, _externalIndex, _boardWorkspace, transparent, snapshot);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Information("shutdown", "Application exit.", new { e.ApplicationExitCode });
        _externalIndex?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
