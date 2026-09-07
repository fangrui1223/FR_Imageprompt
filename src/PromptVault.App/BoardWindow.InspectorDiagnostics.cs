using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunInspectorSmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8-05 属性烟测");
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
        var before = new System.Windows.Size(BoardViewport.ActualWidth, BoardViewport.ActualHeight);

        var item = _items.First(candidate => File.Exists(ResolveItemOriginalPath(candidate)));
        _selectedIds.Clear();
        _selectedIds.Add(item.Id);
        _selectedNoteIds.Clear();
        ToggleInspector();
        await WaitForLayoutAsync();
        var singleContent = InspectorDetailsText.Text;
        var singleComplete = singleContent.Contains("宽高比", StringComparison.Ordinal)
            && singleContent.Contains("旋转", StringComparison.Ordinal)
            && singleContent.Contains("裁剪", StringComparison.Ordinal);
        var during = new System.Windows.Size(BoardViewport.ActualWidth, BoardViewport.ActualHeight);
        var viewportUnchanged = NearlyEqual(before.Width, during.Width) && NearlyEqual(before.Height, during.Height);

        _selectedIds.Add(_items.First(candidate => candidate.Id != item.Id).Id);
        UpdateInspectorContent();
        var multiComplete = SelectionSummaryText.Text.Contains("2", StringComparison.Ordinal)
            && InspectorDetailsText.Text.Contains("共同分组", StringComparison.Ordinal);

        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        _selectedNoteIds.Add(_notes.First().Id);
        UpdateInspectorContent();
        var noteComplete = SelectionSummaryText.Text == "便签属性"
            && NotePropertiesPanel.Visibility == Visibility.Visible
            && NoteFontSizeSlider.Value is >= 8 and <= 300;

        _selectedNoteIds.Clear();
        UpdateInspectorContent();
        var canvasComplete = SelectionSummaryText.Text == "画板属性"
            && InspectorDetailsText.Text.Contains("背景", StringComparison.Ordinal);

        var missing = _items.First(candidate => !File.Exists(ResolveItemOriginalPath(candidate)));
        _selectedIds.Add(missing.Id);
        UpdateInspectorContent();
        var missingComplete = SourceStateText.Text.Contains("原图缺失", StringComparison.Ordinal)
            && SourceStateText.TextWrapping == TextWrapping.Wrap;

        GroupNameBox.Focus();
        InspectorCloseTimerTick(null, EventArgs.Empty);
        var focusProtected = BoardInspector.Visibility == Visibility.Visible;
        CloseInspector();
        var closedWithoutPlaceholder = BoardInspector.Visibility == Visibility.Collapsed
            && NearlyEqual(before.Width, BoardViewport.ActualWidth)
            && NearlyEqual(before.Height, BoardViewport.ActualHeight);

        var dpi = VisualTreeHelper.GetDpi(this);
        var process = Process.GetCurrentProcess();
        var passed = singleComplete && multiComplete && noteComplete && canvasComplete
            && missingComplete && viewportUnchanged && focusProtected && closedWithoutPlaceholder;
        var report = new
        {
            Milestone = "M8-05-transient-inspector-ui-smoke",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Inspector = new
            {
                SingleImageComplete = singleComplete,
                MultiSelectionComplete = multiComplete,
                NoteComplete = noteComplete,
                CanvasComplete = canvasComplete,
                MissingSourceAndLongPathWrap = missingComplete,
                FocusProtected = focusProtected,
                ViewportUnchanged = viewportUnchanged,
                ClosedWithoutPlaceholder = closedWithoutPlaceholder,
                ViewportWidth = before.Width,
                ViewportHeight = before.Height
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
        SetStatus(passed ? "M8-05 隔离属性烟测通过" : "M8-05 隔离属性烟测失败");
    }
}
