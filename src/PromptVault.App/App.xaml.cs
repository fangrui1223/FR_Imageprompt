using System.Windows;
using System.Windows.Threading;
using PromptVault.App.Services;
using PromptVault.Core;
using PromptVault.Licensing;

namespace PromptVault.App;

public partial class App : System.Windows.Application
{
    private TrayService? _tray;
    private AppSettings? _settings;
    private LibraryRepository? _repository;
    private CaptureCoordinator? _capture;
    private ExternalFolderIndexService? _externalIndex;
    private BoardWorkspaceService? _boardWorkspace;
    private MainWindowHandoffMetrics? _lastMainWindowHandoff;

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
        string? boardM85SmokeReport = null;
        string? boardM10SmokeReport = null;
        string? m10TransparentSmokeReport = null;
        string? galleryPartialCardSmokeReport = null;
        string? m102TransparentHandoffSmokeReport = null;
        try
        {
            var licenseWindowSmokeReport = GetOptionValue(e.Args, "--license-window-smoke");
            var licenseMainSmokeReport = GetOptionValue(e.Args, "--license-main-smoke");
            var licenseRequestOutput = GetOptionValue(e.Args, "--license-request-out");
            if (licenseRequestOutput is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(licenseRequestOutput)!);
                await File.WriteAllTextAsync(licenseRequestOutput, WindowsDeviceIdentity.GetRequestCode());
                Shutdown(0);
                return;
            }
            var licenseService = new ProductLicenseService(GetOptionValue(e.Args, "--license"));
            var licenseValidation = licenseService.ValidateCurrent();
            if (!licenseValidation.IsValid)
            {
                AppLog.Warning("license", "License validation blocked startup.", data: new
                {
                    status = licenseValidation.Status.ToString(),
                    explicitPath = licenseService.UsesExplicitPath
                });
                if (isAiWorker)
                {
                    Shutdown(3);
                    return;
                }

                var activation = new LicenseActivationWindow(licenseService, licenseValidation);
                if (licenseWindowSmokeReport is not null)
                {
                    activation.Show();
                    await M9LicenseDiagnostics.CaptureActivationAsync(activation, licenseValidation, licenseWindowSmokeReport);
                    activation.Close();
                    Shutdown(0);
                    return;
                }
                if (activation.ShowDialog() != true)
                {
                    Shutdown(3);
                    return;
                }
                licenseValidation = licenseService.ValidateCurrent();
                if (!licenseValidation.IsValid)
                {
                    MessageBox.Show(
                        licenseValidation.Message,
                        "许可证验证失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown(3);
                    return;
                }
            }
            AppLog.Information("license", "License validation succeeded.", data: new
            {
                licenseId = licenseValidation.License?.LicenseId,
                expiresAtUtc = licenseValidation.License?.ExpiresAtUtc,
                majorVersion = ProductLicensePolicy.GetCurrentMajorVersion(typeof(App).Assembly)
            });

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
            boardM85SmokeReport = GetOptionValue(e.Args, "--board-m85-smoke");
            boardM10SmokeReport = GetOptionValue(e.Args, "--board-m10-smoke");
            m10TransparentSmokeReport = GetOptionValue(e.Args, "--m10-transparent-smoke");
            m102TransparentHandoffSmokeReport = GetOptionValue(e.Args, "--m102-transparent-handoff-smoke");
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
            _tray = new TrayService(window, () => _ = RequestExitAsync());
            var m112Report = GetOptionValue(e.Args, "--m112-smoke");
            if (m112Report is not null) window.BeginM112StartupSampling();
            window.Show();
            if (m112Report is not null)
            {
                var passed = await RunM112SmokeAsync(window, m112Report);
                Shutdown(passed ? 0 : 2);
                return;
            }
            if (GetOptionValue(e.Args, "--board-m111-smoke") is { } m111Report)
            {
                var passed = await RunM111BoardSmokeAsync(m111Report);
                Shutdown(passed ? 0 : 2);
                return;
            }
            if (GetOptionValue(e.Args, "--m11-stability-smoke") is { } m11Report)
            {
                var passed = await RunM11StabilitySmokeAsync(m11Report);
                Shutdown(passed ? 0 : 2);
                return;
            }
            if (m102TransparentHandoffSmokeReport is not null)
            {
                var passed = await RunM102TransparentHandoffStressAsync(
                    m102TransparentHandoffSmokeReport,
                    _settings,
                    e.Args.Contains("--short-handoff-smoke", StringComparer.OrdinalIgnoreCase) ? 3 : 20);
                Shutdown(passed ? 0 : 2);
                return;
            }
            if (m10TransparentSmokeReport is not null)
            {
                var passed = await window.RunM10TransparentSmokeAsync(m10TransparentSmokeReport, _settings);
                window.AllowClose();
                window.Close();
                Shutdown(passed ? 0 : 2);
                return;
            }
            if (licenseMainSmokeReport is not null)
            {
                await M9LicenseDiagnostics.CaptureMainAsync(window, _settings, licenseValidation, licenseMainSmokeReport);
                window.AllowClose();
                window.Close();
                Shutdown(0);
                return;
            }
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
            if (openBoardForDiagnostics || boardCameraSmokeReport is not null || boardInputSmokeReport is not null || boardCommandSmokeReport is not null || boardChromeSmokeReport is not null || boardInspectorSmokeReport is not null || boardTransformSmokeReport is not null || boardImageSmokeReport is not null || boardM8GateReport is not null || boardM82SmokeReport is not null || boardM83SmokeReport is not null || boardM84SmokeReport is not null || boardM85SmokeReport is not null || boardM10SmokeReport is not null)
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
                if (boardM85SmokeReport is not null)
                {
                    var passed = await boardWindow.RunM85SmokeAsync(boardM85SmokeReport, _settings);
                    Shutdown(passed ? 0 : 2);
                    return;
                }
                if (boardM10SmokeReport is not null)
                {
                    var passed = await boardWindow.RunM10SmokeAsync(boardM10SmokeReport, _settings);
                    Shutdown(passed ? 0 : 2);
                    return;
                }
                if (boardCameraSmokeReport is not null
                    || boardInputSmokeReport is not null
                    || boardCommandSmokeReport is not null
                    || boardChromeSmokeReport is not null
                    || boardInspectorSmokeReport is not null
                    || boardTransformSmokeReport is not null
                    || boardImageSmokeReport is not null
                    || boardM8GateReport is not null
                    || boardM82SmokeReport is not null
                    || boardM83SmokeReport is not null
                    || boardM84SmokeReport is not null)
                {
                    Shutdown(0);
                    return;
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

    private bool _mainWindowSwitchActive;
    private bool _exitActive;

    private async Task RequestExitAsync()
    {
        if (_exitActive || _mainWindowSwitchActive) return;
        _exitActive = true;
        var boards = Windows.OfType<BoardWindow>().Select(window => (Window: window, Enabled: window.IsEnabled)).ToArray();
        foreach (var entry in boards) entry.Window.IsEnabled = false;
        try
        {
            foreach (var entry in boards)
                if (!await entry.Window.PrepareForApplicationExitAsync()) return;
            foreach (var entry in boards) entry.Window.ApproveApplicationExit();
            Shutdown();
        }
        finally
        {
            foreach (var entry in boards) if (entry.Window.IsVisible) entry.Window.IsEnabled = entry.Enabled;
            _exitActive = false;
        }
    }

    internal async Task<bool> SwitchMainWindowAsync(MainWindow source, bool transparent)
    {
        if (_repository is null || _capture is null || _settings is null || _externalIndex is null) return false;
        if (_mainWindowSwitchActive || _exitActive || !ReferenceEquals(MainWindow, source)) return false;
        _mainWindowSwitchActive = true;
        var oldWindow = MainWindow as MainWindow;
        MainWindow? next = null;
        try
        {
            var snapshot = await source.BeginSnapshotTransferAsync();
            next = CreateMainWindow(transparent, snapshot, transitionStaging: true);
            next.Opacity = 0;
            next.IsEnabled = false;
            next.ShowActivated = false;
            next.ShowInTaskbar = false;
            next.Show();
            await next.TransitionReady.WaitAsync(TimeSpan.FromSeconds(5));
            var oldVisibleUntilReady = oldWindow?.IsVisible != false;
            var replacementHiddenUntilReady = next.Opacity <= 0.001 && !next.IsEnabled;
            var preparedFrames = next.TransitionPreparedRenderFrames;
            await Dispatcher.InvokeAsync(() =>
            {
                next.CommitStagedVisualMode();
                MainWindow = next;
                _tray?.UpdateWindow(next);
                next.ShowInTaskbar = true;
                next.IsEnabled = true;
                next.Opacity = 1;
                next.Activate();
                if (oldWindow is null) return;
                oldWindow.AllowClose();
                oldWindow.Close();
            }, DispatcherPriority.Send);
            _lastMainWindowHandoff = new MainWindowHandoffMetrics(
                true,
                oldVisibleUntilReady,
                replacementHiddenUntilReady,
                preparedFrames,
                next.IsVisible && next.Opacity >= 0.999,
                oldWindow is null || !oldWindow.IsVisible,
                null);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("transparent-handoff", "Transparent window handoff failed; the current window was preserved.", ex);
            oldWindow?.CancelSnapshotTransfer();
            if (next is not null)
            {
                next.AbandonStagedWindow();
                next.AllowClose();
                next.Close();
            }
            if (oldWindow is not null)
            {
                MainWindow = oldWindow;
                _tray?.UpdateWindow(oldWindow);
                oldWindow.Show();
                oldWindow.Activate();
            }
            _lastMainWindowHandoff = new MainWindowHandoffMetrics(
                false,
                oldWindow?.IsVisible == true,
                next?.Opacity <= 0.001,
                next?.TransitionPreparedRenderFrames ?? 0,
                false,
                false,
                ex.Message);
            return false;
        }
        finally
        {
            _mainWindowSwitchActive = false;
        }
    }

    private async Task<bool> RunM102TransparentHandoffStressAsync(
        string reportPath,
        AppSettings settings,
        int cyclesPerDirection = 20)
    {
        reportPath = Path.GetFullPath(reportPath);
        var samples = new List<M102HandoffStressSample>(160);
        var retiredWindows = new List<WeakReference<PromptVault.App.MainWindow>>(160);
        Exception? failure = null;
        var positions = new[] { 0d, 0.2d, 0.5d, 0.9d };
        var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var privateMemoryBefore = process.PrivateMemorySize64;
        try
        {
            foreach (var position in positions)
            {
                for (var cycle = 1; cycle <= cyclesPerDirection; cycle++)
                {
                    foreach (var targetTransparent in new[] { true, false })
                    {
                        if (MainWindow is not PromptVault.App.MainWindow source)
                            throw new InvalidOperationException("M10.2 交接期间主窗口不存在。");
                        if (source.IsTransparentForDiagnostics == targetTransparent)
                            throw new InvalidOperationException("M10.2 交接方向状态异常。");
                        var snapshot = await source.PrepareM102HandoffProbeAsync(settings, position);
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        var switched = await SwitchMainWindowAsync(source, targetTransparent);
                        clock.Stop();
                        if (!switched || MainWindow is not PromptVault.App.MainWindow replacement)
                            throw new InvalidOperationException("M10.2 窗口交接失败。");
                        retiredWindows.Add(new WeakReference<PromptVault.App.MainWindow>(source));
                        var metrics = _lastMainWindowHandoff;
                        var validation = await replacement.ValidateM102HandoffAsync(
                            settings,
                            snapshot,
                            metrics);
                        samples.Add(new M102HandoffStressSample(
                            position,
                            cycle,
                            targetTransparent ? "normal-to-transparent" : "transparent-to-normal",
                            clock.Elapsed.TotalMilliseconds,
                            metrics,
                            validation));
                        if (!validation.Passed)
                            throw new InvalidOperationException("M10.2 窗口交接状态验证失败。");
                        source = null!;
                        replacement = null!;
                        snapshot = null!;
                        // Real users leave an idle turn between toggles. Give WPF the
                        // same opportunity to retire the old HwndSource before the
                        // stress loop starts the next replacement transaction.
                        await Dispatcher.InvokeAsync(
                            static () => { },
                            DispatcherPriority.ContextIdle);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            AppLog.Warning("m10.2-handoff-stress", "Transparent handoff stress failed.", ex);
        }

        var directionCounts = samples
            .GroupBy(sample => sample.Direction)
            .ToDictionary(group => group.Key, group => group.Count());
        var handoffsPassed = failure is null
            && samples.Count == positions.Length * 2 * cyclesPerDirection
            && samples.All(sample => sample.Validation.Passed)
            && directionCounts.GetValueOrDefault("normal-to-transparent") == positions.Length * cyclesPerDirection
            && directionCounts.GetValueOrDefault("transparent-to-normal") == positions.Length * cyclesPerDirection;
        var elapsed = samples.Select(sample => sample.ElapsedMilliseconds).Order().ToArray();
        process.Refresh();
        var workingSetBeforeCollection = process.WorkingSet64;
        var privateMemoryBeforeCollection = process.PrivateMemorySize64;
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(250);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(250);
        process.Refresh();
        var retiredWindowsAliveAfterCollection = retiredWindows.Count(reference =>
            reference.TryGetTarget(out _));
        var retirementCounts = new List<int> { retiredWindowsAliveAfterCollection };
        // WPF weak-event cleanup runs on the dispatcher after collection. Collect again
        // after those deferred releases, keeping the original <= 1 live-window gate.
        for (var attempt = 0; retiredWindowsAliveAfterCollection > 1 && attempt < 5; attempt++)
        {
            await Task.Delay(500);
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            retiredWindowsAliveAfterCollection = retiredWindows.Count(reference => reference.TryGetTarget(out _));
            retirementCounts.Add(retiredWindowsAliveAfterCollection);
        }
        process.Refresh();
        var allPassed = handoffsPassed && retiredWindowsAliveAfterCollection <= 1;
        var report = new
        {
            Milestone = "M10.2-transparent-prewarmed-atomic-handoff-stress",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Passed = allPassed,
            Matrix = new
            {
                ScrollPositions = positions,
                CyclesPerDirectionPerPosition = cyclesPerDirection,
                ExpectedHandoffs = positions.Length * 2 * cyclesPerDirection,
                ActualHandoffs = samples.Count,
                DirectionCounts = directionCounts,
                PassedHandoffs = samples.Count(sample => sample.Validation.Passed),
                MinimumPreparedRenderFrames = samples.Count == 0
                    ? 0
                    : samples.Min(sample => sample.Validation.PreparedRenderFrames),
                MaximumOffsetError = samples.Count == 0
                    ? double.NaN
                    : samples.Max(sample => Math.Abs(
                        sample.Validation.ExpectedVerticalOffset
                        - sample.Validation.ActualVerticalOffset)),
                MaximumGalleryReflows = samples.Count == 0
                    ? -1
                    : samples.Max(sample => sample.Validation.GalleryReflows),
                ElapsedP50Ms = Percentile(elapsed, 0.5),
                ElapsedP95Ms = Percentile(elapsed, 0.95),
                ElapsedMaximumMs = elapsed.Length == 0 ? 0 : elapsed[^1]
            },
            Protocol = new
            {
                ScreenshotOverlayUsed = false,
                AnimationUsed = false,
                OldWindowVisibleUntilReplacementStable = samples.All(sample =>
                    sample.Metrics?.OldWindowVisibleUntilReady == true),
                ReplacementHiddenUntilStable = samples.All(sample =>
                    sample.Metrics?.ReplacementHiddenUntilReady == true),
                AtomicDispatcherCommit = true,
                FirstScreenFallbackPathUsed = false
            },
            Process = new
            {
                WorkingSetBefore = workingSetBefore,
                PrivateMemoryBefore = privateMemoryBefore,
                WorkingSetBeforeCollection = workingSetBeforeCollection,
                PrivateMemoryBeforeCollection = privateMemoryBeforeCollection,
                WorkingSetAfterCollection = process.WorkingSet64,
                PrivateMemoryAfterCollection = process.PrivateMemorySize64,
                RetiredWindowReferences = retiredWindows.Count,
                RetiredWindowsAliveAfterCollection = retiredWindowsAliveAfterCollection,
                RetirementCounts = retirementCounts,
                RetiredWindowStates = retiredWindows.Select(reference => reference.TryGetTarget(out var retired)
                    ? retired.DescribeRetiredWindowForDiagnostics() : null).Where(state => state is not null).ToArray(),
                process.HandleCount,
                ThreadCount = process.Threads.Count,
                process.Responding
            },
            DataSafety = new
            {
                SettingsPath = settings.StorageFilePath,
                settings.LibraryRoot,
                settings.CaptureListeningEnabled,
                settings.CaptureQuickEditEnabled,
                settings.OnlineAiEnabled,
                RealLibraryOpened = false,
                ClipboardUsed = false,
                NetworkUsed = false,
                ApiKeyUsed = false
            },
            Error = failure?.ToString(),
            Samples = samples
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return allPassed;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return Math.Round(sorted[index], 3);
    }

    private sealed record M102HandoffStressSample(
        double ScrollPosition,
        int Cycle,
        string Direction,
        double ElapsedMilliseconds,
        MainWindowHandoffMetrics? Metrics,
        MainWindowHandoffValidation Validation);

    private MainWindow CreateMainWindow(
        bool transparent,
        MainWindowSnapshot? snapshot,
        bool transitionStaging = false)
    {
        if (_repository is null || _capture is null || _settings is null || _externalIndex is null || _boardWorkspace is null) throw new InvalidOperationException("FR_Imageprompt 尚未完成初始化。");
        return new MainWindow(
            _repository,
            _capture,
            _settings,
            _externalIndex,
            _boardWorkspace,
            transparent,
            snapshot,
            transitionStaging);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Information("shutdown", "Application exit.", new { e.ApplicationExitCode });
        _externalIndex?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
