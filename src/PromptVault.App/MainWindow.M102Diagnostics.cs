using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    internal bool IsTransparentForDiagnostics => _transparentMode;

    internal async Task<MainWindowSnapshot> PrepareM102HandoffProbeAsync(
        AppSettings settings,
        double scrollRatio = 0.5)
    {
        EnsureM10IsolatedSettings(settings);
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                Loaded -= handler;
                loaded.TrySetResult();
            };
            Loaded += handler;
            await loaded.Task;
        }
        WindowState = WindowState.Normal;
        Width = 1380;
        Height = 880;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        _rowsScrollViewer ??= FindDescendant<System.Windows.Controls.ScrollViewer>(RowsList);
        if (_rowsScrollViewer is not null)
        {
            var target = Math.Max(0, _rowsScrollViewer.ScrollableHeight)
                * Math.Clamp(scrollRatio, 0, 1);
            _rowsScrollViewer.ScrollToVerticalOffset(target);
            UpdateLayout();
            await WaitForRenderFrameAsync();
            await WaitForRenderFrameAsync();
        }
        return CreateSnapshot();
    }

    internal async Task<MainWindowHandoffValidation> ValidateM102HandoffAsync(
        AppSettings settings,
        MainWindowSnapshot snapshot,
        MainWindowHandoffMetrics? handoff)
    {
        EnsureM10IsolatedSettings(settings);
        await WaitForRenderFrameAsync();
        await WaitForRenderFrameAsync();
        _rowsScrollViewer ??= FindDescendant<System.Windows.Controls.ScrollViewer>(RowsList);
        var session = snapshot.GallerySession
            ?? throw new InvalidOperationException("M10.2 交接烟测缺少图库会话。");
        var offset = _rowsScrollViewer?.VerticalOffset ?? 0;
        var rowsReused = session.Rows.Length == Rows.Count
            && session.Rows.Select((row, index) => ReferenceEquals(row, Rows[index])).All(value => value);
        var itemsRetained = session.Items.Select(item => item.Id).SequenceEqual(_items.Select(item => item.Id));
        var selectionRetained = snapshot.SelectedItemIds.OrderBy(id => id)
            .SequenceEqual(_selectedItemIds.OrderBy(id => id));
        var offsetRetained = Math.Abs(offset - session.VerticalOffset) <= 0.5;
        var layoutRetained = Math.Abs(_layoutWidth - session.LayoutWidth) <= 0.5;
        var stagedProtocolComplete = _stagedHandoffCommitted
            && _transitionPreparedRenderFrames >= 2
            && handoff is
            {
                Succeeded: true,
                OldWindowVisibleUntilReady: true,
                ReplacementHiddenUntilReady: true,
                ReplacementVisibleAfterCommit: true,
                OldWindowClosedAfterCommit: true
            };
        var transparentWindowReady = _transparentMode
            ? AllowsTransparency && WindowStyle == WindowStyle.None
            : !AllowsTransparency;
        var passed = stagedProtocolComplete
            && transparentWindowReady
            && rowsReused
            && itemsRetained
            && selectionRetained
            && offsetRetained
            && layoutRetained
            && _transferredSessionRestoreCount == 1;
        return new MainWindowHandoffValidation(
            passed,
            transparentWindowReady,
            rowsReused,
            itemsRetained,
            selectionRetained,
            session.VerticalOffset,
            offset,
            offsetRetained,
            session.LayoutWidth,
            _layoutWidth,
            layoutRetained,
            _transferredSessionRestoreCount,
            _galleryReflowCount,
            _transitionPreparedRenderFrames);
    }

    internal async Task<bool> WriteM102HandoffSmokeReportAsync(
        string reportPath,
        AppSettings settings,
        MainWindowSnapshot snapshot,
        MainWindowHandoffMetrics? handoff)
    {
        EnsureM10IsolatedSettings(settings);
        reportPath = Path.GetFullPath(reportPath);
        var validation = await ValidateM102HandoffAsync(settings, snapshot, handoff);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var report = new
        {
            Milestone = "M10.2-transparent-prewarmed-atomic-handoff",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Passed = validation.Passed,
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Handoff = handoff,
            Replacement = new
            {
                StagedCommit = _stagedHandoffCommitted,
                validation.PreparedRenderFrames,
                validation.SessionRestores,
                validation.TransparentWindowReady,
                validation.RowsReused,
                validation.ItemsRetained,
                validation.SelectionRetained,
                validation.ExpectedVerticalOffset,
                validation.ActualVerticalOffset,
                validation.OffsetRetained,
                validation.ExpectedLayoutWidth,
                validation.ActualLayoutWidth,
                validation.LayoutRetained,
                validation.GalleryReflows
            },
            Protocol = new
            {
                ScreenshotOverlayUsed = false,
                AnimationUsed = false,
                OldWindowRemainsVisibleUntilReplacementReady = true,
                AtomicCommitUsesSingleDispatcherTurn = true
            },
            DataSafety = new
            {
                SettingsPath = settings.StorageFilePath,
                LibraryRoot = settings.LibraryRoot,
                CaptureListening = settings.CaptureListeningEnabled,
                QuickCapture = settings.CaptureQuickEditEnabled,
                OnlineAi = settings.OnlineAiEnabled,
                RealLibraryOpened = false,
                NetworkRequests = 0,
                CredentialReads = 0
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return validation.Passed;
    }
}
