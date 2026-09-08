using System.Windows.Media;
using System.Windows.Controls;
using System.Windows;

namespace PromptVault.App;

public partial class MainWindow
{
    internal object DescribeRetiredWindowForDiagnostics() => new
    {
        Transparent = _transparentMode, IsVisible, IsLoaded,
        LifetimeCancelled = _windowLifetime.IsCancellationRequested,
        EdgeTimer = _edgeIntentTimer.IsEnabled, TopTimer = _topHideTimer.IsEnabled,
        LeftTimer = _leftHideTimer.IsEnabled, ThumbnailTimer = _thumbnailIdleTimer.IsEnabled,
        ResizeTimer = _resizeTimer.IsEnabled, StatusTimer = _subtleStatusTimer.IsEnabled,
        TopAnimated = TopPanelTransform.HasAnimatedProperties, LeftAnimated = LeftPanelTransform.HasAnimatedProperties
    };

    private List<M112StartupSample>? _m112StartupSamples;

    internal void BeginM112StartupSampling()
    {
        EnsureM10IsolatedSettings(_settings);
        if (!_repository.Paths.Root.Contains($"{Path.DirectorySeparatorChar}.m10-isolated{Path.DirectorySeparatorChar}m11-2-",
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("M11.2 requires its synthetic library root.");
        _m112StartupSamples = [];
        CompositionTarget.Rendering += M112StartupRendering;
    }

    private void M112StartupRendering(object? sender, EventArgs e) => RecordM112StartupSample("render");

    private void RecordM112StartupSample(string stage)
    {
        if (_m112StartupSamples is null) return;
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        var cards = Rows.SelectMany(row => row.LayoutItems.Select(card =>
            new M112Card(card.Item.Id, card.LayoutX, row.PanelY + card.LayoutY,
                card.LayoutWidth, card.ImageHeight))).Take(12).ToArray();
        var sample = new M112StartupSample(stage, _items.Count, _layoutWidth,
            _rowsScrollViewer?.ViewportWidth ?? 0, RowsList.ActualWidth, cards,
            stage == "apply" ? [] : FindRealizedGalleryCards()
                .Where(card => card.ActualWidth > 0 && card.ActualHeight > 0)
                .Select(card =>
                {
                    var point = card.TranslatePoint(new Point(), RowsList);
                    return new M112Card(((GalleryCardViewModel)card.DataContext).Id,
                        point.X, point.Y, card.ActualWidth, card.ActualHeight);
                })
                .Where(card => card.Y >= 0 && card.Y < RowsList.ActualHeight)
                .OrderBy(card => card.Id).ToArray());
        var previous = _m112StartupSamples.LastOrDefault();
        if (previous is null || previous.Stage != stage || previous.Items != sample.Items
            || Math.Abs(previous.ViewportWidth - sample.ViewportWidth) > 0.01
            || !previous.Cards.SequenceEqual(sample.Cards)
            || !previous.VisualCards.SequenceEqual(sample.VisualCards)) _m112StartupSamples.Add(sample);
    }

    internal async Task<object> FinishM112StartupSamplingAsync()
    {
        try
        {
            await TransitionReady.WaitAsync(TimeSpan.FromSeconds(30));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (_isFastBrowseIndexing && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (_isFastBrowseIndexing) throw new TimeoutException("Thin metadata index did not finish.");
            await Task.Delay(500);
            RecordM112StartupSample("settled");
            var populated = _m112StartupSamples!.Where(sample => sample.Cards.Length > 0).ToArray();
            var first = populated.FirstOrDefault();
            var stable = first is null || populated.All(sample => first.Cards.All(card =>
                sample.Cards.FirstOrDefault(candidate => candidate.Id == card.Id) is { } later
                && Math.Abs(card.X - later.X) < 0.01 && Math.Abs(card.Y - later.Y) < 0.01
                && Math.Abs(card.Width - later.Width) < 0.01 && Math.Abs(card.Height - later.Height) < 0.01));
            var rendered = _m112StartupSamples!.Where(sample => sample.VisualCards.Length > 0).ToArray();
            var firstVisual = rendered.FirstOrDefault();
            var stableVisual = firstVisual is null ? _items.Count == 0 : rendered.All(sample =>
                firstVisual.VisualCards.All(card => sample.VisualCards.FirstOrDefault(later => later.Id == card.Id)
                    is { } later && Math.Abs(card.X - later.X) < 0.01 && Math.Abs(card.Y - later.Y) < 0.01
                    && Math.Abs(card.Width - later.Width) < 0.01 && Math.Abs(card.Height - later.Height) < 0.01));
            return new { Passed = stable && stableVisual, LayoutStable = stable, RenderedCardsStable = stableVisual,
                AllMetadataLoaded = _items.Count == _totalCount,
                Reflows = _galleryReflowCount, Samples = _m112StartupSamples!.ToArray() };
        }
        finally
        {
            CompositionTarget.Rendering -= M112StartupRendering;
            _m112StartupSamples = null;
        }
    }

    private sealed record M112Card(long Id, double X, double Y, double Width, double Height);
    private sealed record M112StartupSample(string Stage, int Items, double LayoutWidth,
        double ViewportWidth, double HostWidth, M112Card[] Cards, M112Card[] VisualCards);
}
