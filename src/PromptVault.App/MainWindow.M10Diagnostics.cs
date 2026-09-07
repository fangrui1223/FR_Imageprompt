using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    internal async Task<bool> RunM10TransparentSmokeAsync(
        string reportPath,
        AppSettings settings)
    {
        EnsureM10IsolatedSettings(settings);
        reportPath = Path.GetFullPath(reportPath);
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
        Left = Math.Max(0, SystemParameters.WorkArea.Left + 24);
        Top = Math.Max(0, SystemParameters.WorkArea.Top + 24);
        await WaitForM10LayoutAsync(360);

        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        var contentViewportWidth = _rowsScrollViewer?.ViewportWidth ?? 0;
        var rightmostCard = Rows
            .SelectMany(row => row.LayoutItems.Select(item => row.PanelX + item.LayoutX + item.LayoutWidth))
            .DefaultIfEmpty(0)
            .Max();
        var rightSafetyInset = contentViewportWidth - rightmostCard;
        var rightEdgeFullyVisible = rightSafetyInset >= GalleryViewportWidthPolicy.RightVisualSafetyInset - 0.5;
        var anchorId = FirstVisibleGalleryItemId();
        var geometryBefore = CaptureM10Geometry(anchorId);
        var rowsBefore = Rows.ToArray();
        var cardsBefore = rowsBefore.SelectMany(row => row.Items).ToArray();
        var thumbnailsBefore = cardsBefore.Select(card => card.Thumbnail).ToArray();
        var selectionBefore = _selectedItemIds.OrderBy(id => id).ToArray();
        var reflowsBeforeToggle = _galleryReflowCount;
        var applyBefore = _transparentModeApplyCount;

        _transparentMode = true;
        ApplyTransparentMode();
        await WaitForM10LayoutAsync(120);
        var geometryTransparent = CaptureM10Geometry(anchorId);
        var transparentDidNotReflow = _galleryReflowCount == reflowsBeforeToggle;
        var transparentRetainedRows = SameReferences(rowsBefore, Rows);
        var transparentRetainedCards = SameReferences(cardsBefore, Rows.SelectMany(row => row.Items));
        var transparentRetainedThumbnails = SameReferences(
            thumbnailsBefore,
            Rows.SelectMany(row => row.Items).Select(card => card.Thumbnail));

        _transparentMode = false;
        ApplyTransparentMode();
        await WaitForM10LayoutAsync(120);
        var geometryAfter = CaptureM10Geometry(anchorId);
        var normalDidNotReflow = _galleryReflowCount == reflowsBeforeToggle;
        var exactGeometryRestored = SameM10Layout(geometryBefore, geometryTransparent)
            && SameM10Layout(geometryBefore, geometryAfter);
        var selectionPreserved = selectionBefore.SequenceEqual(_selectedItemIds.OrderBy(id => id));

        var resizeBaseline = _galleryReflowCount;
        Width += 96;
        await WaitForM10LayoutAsync(460);
        var resizeReflows = _galleryReflowCount - resizeBaseline;
        var resizeDebouncedOnce = resizeReflows == 1;

        var snapshot = CreateSnapshot();
        var session = snapshot.GallerySession;
        var transferableSessionComplete = session is not null
            && session.Items.Length == _items.Count
            && session.Rows.Length == Rows.Count
            && Math.Abs(session.VerticalOffset - (_rowsScrollViewer?.VerticalOffset ?? 0)) < 0.5;

        var toggleReflows = _galleryReflowCount - reflowsBeforeToggle - resizeReflows;
        var passed = transparentDidNotReflow
            && normalDidNotReflow
            && exactGeometryRestored
            && transparentRetainedRows
            && transparentRetainedCards
            && transparentRetainedThumbnails
            && selectionPreserved
            && rightEdgeFullyVisible
            && resizeDebouncedOnce
            && transferableSessionComplete;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var report = new
        {
            Milestone = "M10-transparent-zero-reflow",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Passed = passed,
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                dpi.DpiScaleX,
                RefreshRate = "由真界面验收记录补充"
            },
            Toggle = new
            {
                ApplyCalls = _transparentModeApplyCount - applyBefore,
                Reflows = toggleReflows,
                ExactGeometry = exactGeometryRestored,
                ChromeVisibilityChanged = geometryBefore.HeaderOpacity != geometryTransparent.HeaderOpacity
                    && geometryBefore.StatusOpacity != geometryTransparent.StatusOpacity,
                RowsReused = transparentRetainedRows,
                CardsReused = transparentRetainedCards,
                ThumbnailsReused = transparentRetainedThumbnails,
                SelectionPreserved = selectionPreserved,
                GeometryBefore = geometryBefore,
                GeometryTransparent = geometryTransparent,
                GeometryAfter = geometryAfter
            },
            ManualResize = new
            {
                DebounceMilliseconds = 180,
                Reflows = resizeReflows,
                Passed = resizeDebouncedOnce
            },
            RightEdge = new
            {
                ContentViewportWidth = contentViewportWidth,
                RightmostCard = rightmostCard,
                SafetyInset = rightSafetyInset,
                FullyVisible = rightEdgeFullyVisible
            },
            Transfer = new
            {
                Complete = transferableSessionComplete,
                Items = session?.Items.Length ?? 0,
                Rows = session?.Rows.Length ?? 0,
                RestoresObserved = _transferredSessionRestoreCount
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
        ShowSubtleStatus(passed ? "M10 透明零重排烟测通过" : "M10 透明零重排烟测失败");
        return passed;
    }

    private M10GalleryGeometry CaptureM10Geometry(long? anchorId)
    {
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        var anchorTop = anchorId is null ? null : FindGalleryItemTop(anchorId.Value);
        return new M10GalleryGeometry(
            Math.Round(RowsList.ActualWidth, 3),
            Math.Round(RowsList.ActualHeight, 3),
            Math.Round(_layoutWidth, 3),
            Rows.Count,
            Math.Round(_rowsScrollViewer?.VerticalOffset ?? 0, 3),
            anchorId,
            anchorTop is null ? null : Math.Round(anchorTop.Value, 3),
            GalleryHeader.Visibility,
            Math.Round(GalleryHeader.Opacity, 3),
            StatusText.Visibility,
            Math.Round(StatusText.Opacity, 3));
    }

    private long? FirstVisibleGalleryItemId()
    {
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        var offset = _rowsScrollViewer?.VerticalOffset ?? 0;
        return Rows
            .SelectMany(row => row.LayoutItems.Select(item => new
            {
                item.Item.Id,
                Top = row.PanelY + item.LayoutY
            }))
            .OrderBy(item => Math.Abs(item.Top - offset))
            .Select(item => (long?)item.Id)
            .FirstOrDefault();
    }

    private async Task WaitForM10LayoutAsync(int milliseconds)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Task.Delay(milliseconds);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static bool SameM10Layout(M10GalleryGeometry left, M10GalleryGeometry right) =>
        left.Width == right.Width
        && left.Height == right.Height
        && left.LayoutWidth == right.LayoutWidth
        && left.RowCount == right.RowCount
        && left.VerticalOffset == right.VerticalOffset
        && left.AnchorId == right.AnchorId
        && left.AnchorTop == right.AnchorTop;

    private static bool SameReferences<T>(
        IReadOnlyList<T> expected,
        IEnumerable<T> actual)
        where T : class?
    {
        var materialized = actual.ToArray();
        return expected.Count == materialized.Length
            && expected.Select((item, index) => ReferenceEquals(item, materialized[index])).All(result => result);
    }

    private static void EnsureM10IsolatedSettings(AppSettings settings)
    {
        var settingsPath = Path.GetFullPath(settings.StorageFilePath);
        if (!settingsPath.Contains($"{Path.DirectorySeparatorChar}.m8-isolated{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && !settingsPath.Contains($"{Path.DirectorySeparatorChar}.m10-isolated{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("M10 真界面诊断必须使用显式隔离设置文件。");
        }
        if (settings.CaptureListeningEnabled
            || settings.CaptureQuickEditEnabled
            || settings.OnlineAiEnabled)
        {
            throw new InvalidOperationException("M10 真界面诊断要求关闭收录、快速编辑和在线 AI。");
        }
    }

    private sealed record M10GalleryGeometry(
        double Width,
        double Height,
        double LayoutWidth,
        int RowCount,
        double VerticalOffset,
        long? AnchorId,
        double? AnchorTop,
        Visibility HeaderVisibility,
        double HeaderOpacity,
        Visibility StatusVisibility,
        double StatusOpacity);
}
