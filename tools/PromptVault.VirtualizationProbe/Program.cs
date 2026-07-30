using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PromptVault.App;
using PromptVault.Core;

namespace PromptVault.VirtualizationProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var outputPath = Path.GetFullPath(
            args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
                ? args[0]
                : Path.Combine("artifacts", "performance", "virtualization-probe.json"));
        var simulateRegression = args.Contains("--simulate-regression", StringComparer.OrdinalIgnoreCase);
        var activationDelayMs = ReadIntOption(args, "--activation-delay-ms", 0);
        var entries = CreateEntries(30_000);
        _ = GalleryLayoutEngine.CreateRows(entries.Take(8).ToArray(), 2500);
        var layoutBenchmarks = new List<object>();
        IReadOnlyList<GalleryRow> rows = [];
        foreach (var itemCount in new[] { 240, 1_200, 30_000 })
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var layoutClock = Stopwatch.StartNew();
            var measuredRows = GalleryLayoutEngine.CreateRows(entries.Take(itemCount).ToArray(), 2500);
            layoutClock.Stop();
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            layoutBenchmarks.Add(new
            {
                ItemCount = itemCount,
                LayoutMs = Math.Round(layoutClock.Elapsed.TotalMilliseconds, 3),
                AllocatedBytes = allocatedBytes,
                RowCount = measuredRows.Count
            });
            if (itemCount == 30_000) rows = measuredRows;
        }
        var viewportIndexBuildClock = Stopwatch.StartNew();
        var viewportIndex = MasonryViewportIndex.Create(rows);
        viewportIndexBuildClock.Stop();
        var viewportIndexQuerySamples = new List<double>();
        for (var position = 0; position <= 100; position++)
        {
            var queryTop = Math.Max(
                0,
                (viewportIndex.ExtentHeight - 1600) * position / 100d);
            var queryClock = Stopwatch.StartNew();
            _ = viewportIndex.Query(queryTop, queryTop + 1600);
            queryClock.Stop();
            viewportIndexQuerySamples.Add(queryClock.Elapsed.TotalMilliseconds);
        }
        viewportIndexQuerySamples.Sort();
        var incrementalDifferenceCount = CountIncrementalDifferences(
            entries.Take(1_200).ToArray(),
            2500);

        var samples = new List<double>();
        var renderingClockSamples = new List<double>();
        var warmup = TimeSpan.FromMilliseconds(500);
        var measuredDuration = TimeSpan.FromSeconds(3);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var probeView = CreateList(rows);
        var list = probeView.List;
        var resetProbeRows = GalleryLayoutEngine.CreateRows(entries.Take(9).ToArray(), 2500);
        var window = new Window
        {
            Title = "FR_Imageprompt Virtualization Probe",
            Width = 2560,
            Height = 1600,
            Left = 0,
            Top = 0,
            WindowStyle = WindowStyle.None,
            Background = new SolidColorBrush(Color.FromRgb(10, 16, 25)),
            Content = list,
            ShowInTaskbar = true,
            Topmost = true
        };

        var realizedContainers = 0;
        var realizedRows = 0;
        var maximumRealizedContainers = 0;
        var peakManagedBytes = 0L;
        var jumpResults = new List<JumpResult>();
        var runClock = new Stopwatch();
        window.Loaded += (_, _) =>
        {
            window.Activate();
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                list.UpdateLayout();
                list.ItemsSource = resetProbeRows;
                list.UpdateLayout();
                list.ItemsSource = rows;
                list.UpdateLayout();
                var viewer = FindDescendant<ScrollViewer>(list)
                    ?? throw new InvalidOperationException("The probe list did not create a ScrollViewer.");
                var masonryPanel = FindDescendant<VirtualizingMasonryPanel>(list)
                    ?? throw new InvalidOperationException("The probe list did not create a masonry panel.");
                var jumpSequence = new[] { 0d, 0.25d, 0.5d, 0.75d, 1d, 0.75d, 0.5d, 0.25d, 0d };
                for (var jumpIndex = 0; jumpIndex < jumpSequence.Length; jumpIndex++)
                {
                    var percentage = jumpSequence[jumpIndex];
                    viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * percentage);
                    list.UpdateLayout();
                    DrainDispatcher();
                    list.UpdateLayout();
                    DrainDispatcher();
                    var visible = viewportIndex.Query(
                        viewer.VerticalOffset,
                        viewer.VerticalOffset + viewer.ViewportHeight);
                    var cacheHeight = Math.Max(viewer.ViewportHeight, 1) * 2;
                    var cached = viewportIndex.Query(
                        Math.Max(0, viewer.VerticalOffset - cacheHeight),
                        viewer.VerticalOffset + viewer.ViewportHeight + cacheHeight);
                    var visibleSet = visible.ToHashSet();
                    var cachedSet = cached.ToHashSet();
                    var actual = masonryPanel.GetRealizedItemIndices();
                    var actualSet = actual.ToHashSet();
                    probeView.Lifecycle.Synchronize(rows, actualSet);
                    list.UpdateLayout();
                    var missing = visibleSet.Count(index => !actualSet.Contains(index));
                    var extra = actualSet.Count(index => !cachedSet.Contains(index));
                    var unrealizedVisible = visibleSet.Count(
                        index => index >= rows.Count || !rows[index].IsRealized);
                    maximumRealizedContainers = Math.Max(
                        maximumRealizedContainers,
                        actual.Count);
                    jumpResults.Add(new JumpResult(
                        jumpIndex,
                        percentage,
                        Math.Round(viewer.VerticalOffset, 3),
                        visible.Count,
                        cached.Count,
                        actual.Count,
                        missing,
                        extra,
                        unrealizedVisible));
                }
                viewer.ScrollToTop();
                list.UpdateLayout();
                var lastRenderingTime = TimeSpan.Zero;
                var lastCallbackTimestamp = 0L;
                var measuring = true;
                EventHandler? rendering = null;
                rendering = (_, args) =>
                {
                    if (!measuring
                        || args is not RenderingEventArgs renderingArgs
                        || renderingArgs.RenderingTime == lastRenderingTime)
                    {
                        return;
                    }
                    if (lastRenderingTime != TimeSpan.Zero && runClock.Elapsed >= warmup)
                    {
                        renderingClockSamples.Add(
                            (renderingArgs.RenderingTime - lastRenderingTime).TotalMilliseconds);
                        if (lastCallbackTimestamp != 0)
                        {
                            samples.Add(
                                (Stopwatch.GetTimestamp() - lastCallbackTimestamp)
                                * 1000d
                                / Stopwatch.Frequency);
                        }
                    }
                    lastRenderingTime = renderingArgs.RenderingTime;
                    lastCallbackTimestamp = Stopwatch.GetTimestamp();
                    viewer.ScrollToVerticalOffset(
                        Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + 72));
                    peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(false));
                    if (runClock.Elapsed >= warmup + measuredDuration)
                    {
                        measuring = false;
                        CompositionTarget.Rendering -= rendering;
                        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                        {
                            list.UpdateLayout();
                            probeView.Lifecycle.Synchronize(
                                rows,
                                masonryPanel.GetRealizedItemIndices().ToHashSet());
                            realizedContainers = CountDescendants<ListBoxItem>(list);
                            realizedRows = rows.Count(row => row.IsRealized);
                            maximumRealizedContainers = Math.Max(
                                maximumRealizedContainers,
                                masonryPanel.RealizedItemCount);
                            window.Close();
                            application.Shutdown();
                        }));
                    }
                };
                void StartMeasurement()
                {
                    runClock.Start();
                    CompositionTarget.Rendering += rendering;
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + 1);
                }

                if (activationDelayMs <= 0)
                {
                    StartMeasurement();
                }
                else
                {
                    var activationTimer = new DispatcherTimer(
                        DispatcherPriority.ApplicationIdle,
                        window.Dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(activationDelayMs)
                    };
                    activationTimer.Tick += (_, _) =>
                    {
                        activationTimer.Stop();
                        StartMeasurement();
                    };
                    activationTimer.Start();
                }
            }));
        };

        application.Run(window);
        samples.Sort();
        renderingClockSamples.Sort();
        var p95 = simulateRegression ? 99d : Percentile(renderingClockSamples, 0.95);
        var p99 = Percentile(renderingClockSamples, 0.99);
        var maximum = renderingClockSamples.Count == 0
            ? 0
            : Math.Round(renderingClockSamples[^1], 3);
        var frameCadencePassed = renderingClockSamples.Count > 0
                                 && p95 <= 16.949
                                 && p99 <= 33.898
                                 && maximum <= 33.898;
        var structuralPassed = realizedContainers < rows.Count / 10;
        var viewportCompletenessPassed = jumpResults.All(result =>
            result.MissingVisible == 0
            && result.ExtraOutsideCache == 0
            && result.UnrealizedVisible == 0);
        var passed = structuralPassed
                     && viewportCompletenessPassed
                     && frameCadencePassed
                     && !simulateRegression;
        var report = new
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            Environment = new
            {
                ItemCount = entries.Count,
                RowCount = rows.Count,
                LogicalViewportWidth = 2560,
                LogicalViewportHeight = 1600,
                TargetDpiScale = 1.5
            },
            Measurement = new
            {
                WarmupMs = warmup.TotalMilliseconds,
                DurationMs = measuredDuration.TotalMilliseconds
            },
            Layout = new
            {
                Benchmarks = layoutBenchmarks,
                IncrementalDifferenceCount = incrementalDifferenceCount,
                ViewportIndexBuildMs = Math.Round(viewportIndexBuildClock.Elapsed.TotalMilliseconds, 3),
                ViewportIndexQueryP95Ms = Percentile(viewportIndexQuerySamples, 0.95),
                DataSourceResetCount = 2,
                RealizedContainers = realizedContainers,
                RealizedRows = realizedRows,
                MaximumRealizedContainers = maximumRealizedContainers,
                RowRealizeCount = probeView.Lifecycle.RealizeCount,
                RowReleaseCount = probeView.Lifecycle.ReleaseCount,
                PeakManagedBytes = peakManagedBytes
            },
            ViewportCompleteness = new
            {
                Positions = jumpResults,
                MissingVisibleTotal = jumpResults.Sum(result => result.MissingVisible),
                ExtraOutsideCacheTotal = jumpResults.Sum(result => result.ExtraOutsideCache),
                UnrealizedVisibleTotal = jumpResults.Sum(result => result.UnrealizedVisible)
            },
            Frames = new
            {
                Count = renderingClockSamples.Count,
                P95Ms = p95,
                P99Ms = p99,
                MaximumMs = maximum
            },
            CallbackArrivalDiagnostic = new
            {
                Count = samples.Count,
                P95Ms = Percentile(samples, 0.95),
                MaximumMs = samples.Count == 0
                    ? 0
                    : Math.Round(samples[^1], 3),
                DiagnosticOnly = true
            },
            Checks = new
            {
                P95LimitMs = 16.949,
                P99LimitMs = 33.898,
                MaximumLimitMs = 33.898,
                VirtualizationLimit = rows.Count / 10,
                SimulatedRegression = simulateRegression
            },
            StructuralPassed = structuralPassed,
            ViewportCompletenessPassed = viewportCompletenessPassed,
            StandaloneFrameCadencePassed = frameCadencePassed,
            FrameCadenceAuthority = "The product main-window trace is authoritative when supplied to PromptVault.M6Gate; this simplified window remains a diagnostic fallback.",
            Passed = passed
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 2;
    }

    private static ProbeView CreateList(IReadOnlyList<GalleryRow> rows)
    {
        var lifecycle = new ProbeLifecycle();
        var list = new ListBox
        {
            ItemsSource = rows,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetIsDeferredScrollingEnabled(list, false);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(list, new VirtualizationCacheLength(2, 2));
        VirtualizingPanel.SetCacheLengthUnit(list, VirtualizationCacheLengthUnit.Page);

        var panel = new FrameworkElementFactory(typeof(VirtualizingMasonryPanel));
        list.ItemsPanel = new ItemsPanelTemplate(panel);

        var containerStyle = new Style(typeof(ListBoxItem));
        containerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        containerStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        list.ItemContainerStyle = containerStyle;

        var rowItems = new FrameworkElementFactory(typeof(ItemsControl));
        rowItems.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(GalleryRow.RowHeight)));
        rowItems.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(GalleryRow.Items)));
        rowItems.AddHandler(
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(lifecycle.RowLoaded));
        rowItems.AddHandler(
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(lifecycle.RowUnloaded));
        var rowPanel = new FrameworkElementFactory(typeof(Canvas));
        rowItems.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(rowPanel));
        var cardContainerStyle = new Style(typeof(ContentPresenter));
        cardContainerStyle.Setters.Add(new Setter(
            Canvas.LeftProperty,
            new Binding(nameof(GalleryCardViewModel.LayoutX))));
        cardContainerStyle.Setters.Add(new Setter(
            Canvas.TopProperty,
            new Binding(nameof(GalleryCardViewModel.LayoutY))));
        rowItems.SetValue(ItemsControl.ItemContainerStyleProperty, cardContainerStyle);

        var card = new FrameworkElementFactory(typeof(Border));
        card.SetBinding(
            FrameworkElement.WidthProperty,
            new Binding(nameof(GalleryCardViewModel.LayoutWidth)));
        card.SetBinding(
            FrameworkElement.HeightProperty,
            new Binding(nameof(GalleryCardViewModel.CardHeight)));
        card.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(16, 25, 36)));
        card.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(59, 75, 94)));
        card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        card.SetValue(Border.ClipToBoundsProperty, true);

        var cardGrid = new FrameworkElementFactory(typeof(Grid));
        var placeholder = new FrameworkElementFactory(typeof(Border));
        placeholder.SetValue(
            Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(22, 34, 48)));
        cardGrid.AppendChild(placeholder);

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(
            Image.SourceProperty,
            new Binding(nameof(GalleryCardViewModel.Thumbnail)));
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        cardGrid.AppendChild(image);

        var selection = new FrameworkElementFactory(typeof(Border));
        selection.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(75, 209, 255)));
        selection.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        selection.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        selection.SetBinding(
            UIElement.OpacityProperty,
            new Binding(nameof(GalleryCardViewModel.SelectionVisualOpacity)));
        cardGrid.AppendChild(selection);
        card.AppendChild(cardGrid);
        rowItems.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate { VisualTree = card });
        list.ItemTemplate = new DataTemplate { VisualTree = rowItems };
        return new ProbeView(list, lifecycle);
    }

    private static List<GalleryEntry> CreateEntries(int count)
    {
        var start = DateTimeOffset.UtcNow.AddDays(-count);
        return Enumerable.Range(1, count)
            .Select(index =>
            {
                var (width, height) = (index % 8) switch
                {
                    0 => (400, 1600),
                    1 => (900, 1600),
                    2 => (1000, 1500),
                    3 => (1200, 1600),
                    4 => (1200, 1200),
                    5 => (1600, 1200),
                    6 => (1600, 900),
                    _ => (1600, 400)
                };
                return new GalleryEntry(
                    index,
                    $"probe-{index}",
                    "",
                    "",
                    "",
                    width,
                    height,
                    "jpg",
                    "",
                    "",
                    null,
                    "",
                    "",
                    start.AddMinutes(index),
                    null,
                    false,
                    null);
            })
            .ToList();
    }

    private static int CountIncrementalDifferences(
        IReadOnlyList<GalleryEntry> entries,
        double width)
    {
        var incremental = new List<GalleryRow>();
        for (var offset = 0; offset < entries.Count; offset += GalleryVirtualizationPolicy.PageSize)
        {
            var page = entries
                .Skip(offset)
                .Take(GalleryVirtualizationPolicy.PageSize)
                .ToArray();
            var append = GalleryLayoutEngine.CreateAppend(incremental, page, width);
            if (append.ReplaceIncompleteTail && incremental.Count > 0)
            {
                incremental.RemoveAt(incremental.Count - 1);
            }
            incremental.AddRange(append.Rows);
        }

        var expected = GalleryLayoutEngine.CreateRows(entries, width);
        var differences = Math.Abs(expected.Count - incremental.Count);
        for (var index = 0; index < Math.Min(expected.Count, incremental.Count); index++)
        {
            var left = expected[index];
            var right = incremental[index];
            if (left.SourceIndex != right.SourceIndex
                || left.ColumnIndex != right.ColumnIndex
                || Math.Abs(left.PanelX - right.PanelX) > 0.0001
                || Math.Abs(left.PanelY - right.PanelY) > 0.0001
                || Math.Abs(left.PanelWidth - right.PanelWidth) > 0.0001
                || Math.Abs(left.RowHeight - right.RowHeight) > 0.0001)
            {
                differences++;
            }
        }
        return differences;
    }

    private sealed record ProbeView(ListBox List, ProbeLifecycle Lifecycle);

    private sealed record JumpResult(
        int Sequence,
        double Percentage,
        double VerticalOffset,
        int ExpectedVisible,
        int ExpectedCached,
        int ActualRealized,
        int MissingVisible,
        int ExtraOutsideCache,
        int UnrealizedVisible);

    private sealed class ProbeLifecycle
    {
        private readonly LibraryPaths _paths = new(
            Path.Combine(Path.GetTempPath(), "PromptVault-M7.1-VirtualizationProbe"));

        public int RealizeCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public void RowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: GalleryRow row }) return;
            if (!row.IsRealized)
            {
                row.Realize(_paths, _ => false);
                RealizeCount++;
            }
        }

        public void RowUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: GalleryRow row }
                || !row.IsRealized)
            {
                return;
            }

            row.Release();
            ReleaseCount++;
        }

        public void Synchronize(
            IReadOnlyList<GalleryRow> rows,
            IReadOnlySet<int> retainedIndices)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                if (retainedIndices.Contains(index))
                {
                    if (!row.IsRealized)
                    {
                        row.Realize(_paths, _ => false);
                        RealizeCount++;
                    }
                }
                else if (row.IsRealized)
                {
                    row.Release();
                    ReleaseCount++;
                }
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject source) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static int ReadIntOption(
        IReadOnlyList<string> args,
        string option,
        int fallback)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count
                || !int.TryParse(args[index + 1], out var value))
            {
                throw new ArgumentException($"{option} requires an integer value.");
            }
            return Math.Clamp(value, 0, 30_000);
        }
        return fallback;
    }

    private static int CountDescendants<T>(DependencyObject source) where T : DependencyObject
    {
        var count = source is T ? 1 : 0;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            count += CountDescendants<T>(VisualTreeHelper.GetChild(source, index));
        }
        return count;
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0) return 0;
        var index = Math.Clamp(
            (int)Math.Ceiling(ordered.Count * percentile) - 1,
            0,
            ordered.Count - 1);
        return Math.Round(ordered[index], 3);
    }
}
