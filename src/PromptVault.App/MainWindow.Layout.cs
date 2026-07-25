using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private const string LibraryLayoutPreferenceKey = "library";
    private static readonly double[] LayoutSpacingSteps = [8, 14, 22];
    private static readonly double[] LayoutTargetSizeSteps = [240, 320, 400, 480];

    private string CurrentGalleryLayoutPreferenceKey => IsExternalMode
        ? $"external:{_externalFolderId}"
        : LibraryLayoutPreferenceKey;

    private GalleryLayoutPreference CurrentGalleryLayoutPreference() =>
        _settings.GetGalleryLayout(CurrentGalleryLayoutPreferenceKey);

    private GalleryLayoutOptions CurrentGalleryLayoutOptions() =>
        CurrentGalleryLayoutPreference().ToOptions();

    private void LayoutModeClick(object sender, RoutedEventArgs e)
    {
        ChangeGalleryLayout(preference =>
        {
            preference.Mode = string.Equals(
                preference.Mode,
                GalleryLayoutPreference.WaterfallMode,
                StringComparison.Ordinal)
                ? GalleryLayoutPreference.JustifiedMode
                : GalleryLayoutPreference.WaterfallMode;
        });
    }

    private void LayoutDensityClick(object sender, RoutedEventArgs e)
    {
        ChangeGalleryLayout(preference =>
        {
            var next = preference.Density switch
            {
                GalleryLayoutPreference.CompactDensity => GalleryLayoutPreference.ComfortableDensity,
                GalleryLayoutPreference.ComfortableDensity => GalleryLayoutPreference.SpaciousDensity,
                _ => GalleryLayoutPreference.CompactDensity
            };
            preference.ApplyDensity(next);
        });
    }

    private void LayoutSpacingClick(object sender, RoutedEventArgs e)
    {
        ChangeGalleryLayout(preference =>
        {
            preference.Spacing = NextStep(LayoutSpacingSteps, preference.Spacing);
            preference.Density = GalleryLayoutPreference.CustomDensity;
        });
    }

    private void LayoutTargetSizeClick(object sender, RoutedEventArgs e)
    {
        ChangeGalleryLayout(preference =>
        {
            preference.TargetSize = NextStep(LayoutTargetSizeSteps, preference.TargetSize);
            preference.Density = GalleryLayoutPreference.CustomDensity;
        });
    }

    private void ChangeGalleryLayout(Action<GalleryLayoutPreference> update)
    {
        var anchor = CaptureLayoutVisualAnchor();
        var preference = CurrentGalleryLayoutPreference();
        update(preference);
        preference.Normalize();
        _settings.Save();
        UpdateLayoutControlVisuals();
        ReflowGalleryForLayoutPreference(anchor);
        ShowSubtleStatus(
            $"{LayoutModeLabel(preference)} · {LayoutDensityLabel(preference)} · "
            + $"间距 {preference.Spacing:0} · 尺寸 {preference.TargetSize:0}");
    }

    private void ReflowGalleryForLayoutPreference(GalleryLayoutVisualAnchor? anchor)
    {
        var stopwatch = Stopwatch.StartNew();
        var width = GetGalleryAvailableWidth();
        var rows = GalleryLayoutEngine.CreateRows(
            _items,
            width,
            CurrentGalleryLayoutOptions());
        var reusableCards = CreateReusableCardMap(_items);
        var reusableIds = reusableCards.Keys.ToHashSet();
        var additionalRows = rows
            .Select((row, index) => (row, index))
            .Where(candidate => candidate.row.LayoutItems.Any(item => reusableIds.Contains(item.Item.Id)))
            .Select(candidate => candidate.index)
            .ToHashSet();
        ApplyPreparedRows(
            _items.ToArray(),
            rows,
            width,
            new GalleryPreparation(
                reusableCards,
                new Dictionary<long, GalleryCardViewModel>(),
                CountRowsForFirstViewport(rows),
                additionalRows),
            resetScroll: false);
        ApplySelectionState();
        RestoreInspectorAfterRefresh();
        RestoreLayoutVisualAnchor(anchor);
        stopwatch.Stop();
        DevelopmentPerformanceTrace.Event("gallery-layout-switch", new
        {
            source = CurrentGalleryLayoutPreferenceKey,
            mode = CurrentGalleryLayoutPreference().Mode,
            spacing = CurrentGalleryLayoutPreference().Spacing,
            targetSize = CurrentGalleryLayoutPreference().TargetSize,
            rows = Rows.Count,
            reusableCards = reusableCards.Count,
            elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            rowsOpacity = RowsList.Opacity
        });
    }

    private GalleryLayoutVisualAnchor? CaptureLayoutVisualAnchor()
    {
        _rowsScrollViewer ??= FindDescendant<System.Windows.Controls.ScrollViewer>(RowsList);
        var offset = _rowsScrollViewer?.VerticalOffset ?? 0;
        var viewportBottom = offset + (_rowsScrollViewer?.ViewportHeight ?? RowsList.ActualHeight);
        var rowTop = 0d;
        GalleryLayoutVisualAnchor? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var row in Rows)
        {
            foreach (var layout in row.LayoutItems)
            {
                var top = rowTop + layout.LayoutY;
                var bottom = top + layout.ImageHeight;
                if (_selectionFocusId == layout.Item.Id
                    && bottom >= offset
                    && top <= viewportBottom)
                {
                    return new GalleryLayoutVisualAnchor(layout.Item.Id, offset - top);
                }

                var distance = Math.Abs(top - offset);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = new GalleryLayoutVisualAnchor(layout.Item.Id, offset - top);
                }
            }
            rowTop += row.RowHeight + row.RowMargin.Bottom;
        }
        return nearest;
    }

    private void RestoreLayoutVisualAnchor(GalleryLayoutVisualAnchor? anchor)
    {
        if (anchor is null) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _rowsScrollViewer ??= FindDescendant<System.Windows.Controls.ScrollViewer>(RowsList);
            if (_rowsScrollViewer is null) return;
            var top = FindGalleryItemTop(anchor.ItemId);
            if (top is null) return;
            var target = Math.Clamp(
                top.Value + anchor.OffsetFromItemTop,
                0,
                _rowsScrollViewer.ScrollableHeight);
            _rowsScrollViewer.ScrollToVerticalOffset(target);
            QueueThumbnailPriorityRefresh();
        }));
    }

    private double? FindGalleryItemTop(long itemId)
    {
        var rowTop = 0d;
        foreach (var row in Rows)
        {
            var layout = row.LayoutItems.FirstOrDefault(item => item.Item.Id == itemId);
            if (layout is not null) return rowTop + layout.LayoutY;
            rowTop += row.RowHeight + row.RowMargin.Bottom;
        }
        return null;
    }

    private void UpdateLayoutControlVisuals()
    {
        if (!IsInitialized) return;
        var preference = CurrentGalleryLayoutPreference();
        LayoutModeButton.Content = $"布局：{LayoutModeLabel(preference)}";
        LayoutDensityButton.Content = $"密度：{LayoutDensityLabel(preference)}";
        LayoutSpacingButton.Content = $"间距 {preference.Spacing:0}";
        LayoutTargetSizeButton.Content = $"尺寸 {preference.TargetSize:0}";
        LayoutModeButton.ToolTip = "切换瀑布流与等高拼接";
        LayoutDensityButton.ToolTip = "切换紧凑、舒适和宽松预设";
        LayoutSpacingButton.ToolTip = "单独调整图片间距";
        LayoutTargetSizeButton.ToolTip = "单独调整卡片目标尺寸";
    }

    private static string LayoutModeLabel(GalleryLayoutPreference preference) =>
        string.Equals(
            preference.Mode,
            GalleryLayoutPreference.JustifiedMode,
            StringComparison.Ordinal)
            ? "等高"
            : "瀑布";

    private static string LayoutDensityLabel(GalleryLayoutPreference preference) =>
        preference.Density switch
        {
            GalleryLayoutPreference.CompactDensity => "紧凑",
            GalleryLayoutPreference.SpaciousDensity => "宽松",
            GalleryLayoutPreference.CustomDensity => "自定义",
            _ => "舒适"
        };

    private static double NextStep(IReadOnlyList<double> steps, double current)
    {
        foreach (var step in steps)
        {
            if (step > current + 0.1) return step;
        }
        return steps[0];
    }

    private sealed record GalleryLayoutVisualAnchor(
        long ItemId,
        double OffsetFromItemTop);
}
