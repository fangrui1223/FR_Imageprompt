using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PromptVault.App.Services;
using Size = System.Windows.Size;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunChromeSmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8-04 窗口烟测");
        reportPath = Path.GetFullPath(reportPath);
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) => { Loaded -= handler; loaded.TrySetResult(); };
            Loaded += handler;
            await loaded.Task;
        }
        WindowState = WindowState.Maximized;
        await WaitForLayoutAsync();
        HideBoardTopBar();
        await WaitForLayoutAsync();
        var viewportBefore = new Size(BoardViewport.ActualWidth, BoardViewport.ActualHeight);
        var hidden = !BoardTopBar.IsHitTestVisible && BoardTopBar.Opacity <= 0.001;

        ShowBoardTopBar(focusKeyboard: false);
        await WaitForLayoutAsync();
        var viewportDuring = new Size(BoardViewport.ActualWidth, BoardViewport.ActualHeight);
        var shown = BoardTopBar.IsHitTestVisible && BoardTopBar.Opacity >= 0.999;
        var overlayDidNotResizeViewport = NearlyEqual(viewportBefore.Width, viewportDuring.Width)
            && NearlyEqual(viewportBefore.Height, viewportDuring.Height);

        var previous = Topmost;
        var secondaryBoard = await _repository.CreateBoardAsync("M8 窗口同步烟测");
        var secondaryWindow = await _workspace.OpenAsync(this, secondaryBoard.Id);
        await WaitForLayoutAsync();
        ToggleTopmostPreference();
        var topmostEnabled = Topmost != previous
            && secondaryWindow.Topmost == Topmost
            && settings.BoardAlwaysOnTop == Topmost
            && TopmostButton.IsChecked == Topmost;
        ToggleTopmostPreference();
        var topmostRestored = Topmost == previous && settings.BoardAlwaysOnTop == previous;
        secondaryWindow.Close();
        await _repository.DeleteBoardAsync(secondaryBoard.Id);

        WindowState = WindowState.Normal;
        Width = 1200;
        Height = 760;
        await WaitForLayoutAsync();
        var normalResizable = ResizeMode == ResizeMode.CanResize && WindowStyle == WindowStyle.None;
        ToggleMaximized();
        await WaitForLayoutAsync();
        var maximized = WindowState == WindowState.Maximized;
        ToggleMaximized();
        await WaitForLayoutAsync();
        var restored = WindowState == WindowState.Normal;
        ShowTopBarFromKeyboard();
        await WaitForLayoutAsync();
        var keyboardReveal = BoardTopBar.IsHitTestVisible && BoardSelector.IsKeyboardFocusWithin;

        var dpi = VisualTreeHelper.GetDpi(this);
        var process = Process.GetCurrentProcess();
        var passed = hidden
            && shown
            && overlayDidNotResizeViewport
            && topmostEnabled
            && topmostRestored
            && normalResizable
            && maximized
            && restored
            && keyboardReveal;
        var report = new
        {
            Milestone = "M8-04-immersive-window-chrome-ui-smoke",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Chrome = new
            {
                DefaultHidden = hidden,
                IntentRevealVisible = shown,
                KeyboardRevealFocused = keyboardReveal,
                ViewportBefore = new { viewportBefore.Width, viewportBefore.Height },
                ViewportDuring = new { viewportDuring.Width, viewportDuring.Height },
                OverlayDidNotResizeViewport = overlayDidNotResizeViewport,
                Borderless = WindowStyle == WindowStyle.None,
                Resizable = normalResizable,
                Maximized = maximized,
                Restored = restored
            },
            Topmost = new
            {
                EnabledStateSynchronized = topmostEnabled,
                RestoredStateSynchronized = topmostRestored,
                PersistedSetting = settings.BoardAlwaysOnTop
            },
            Process = new
            {
                WorkingSetBytes = process.WorkingSet64,
                PrivateMemoryBytes = process.PrivateMemorySize64,
                HandleCount = process.HandleCount,
                ThreadCount = process.Threads.Count,
                Responding = process.Responding
            },
            DataSafety = IsolatedSafety(settings),
            Passed = passed
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(passed ? "M8-04 隔离窗口烟测通过" : "M8-04 隔离窗口烟测失败");
    }
}
