namespace PromptVault.App;

public sealed class MasonryViewportIndex
{
    private readonly ColumnIndex[] _columns;

    private MasonryViewportIndex(
        int itemCount,
        double extentWidth,
        double extentHeight,
        ColumnIndex[] columns)
    {
        ItemCount = itemCount;
        ExtentWidth = extentWidth;
        ExtentHeight = extentHeight;
        _columns = columns;
    }

    public static MasonryViewportIndex Empty { get; } = new(0, 0, 0, []);

    public int ItemCount { get; }
    public double ExtentWidth { get; }
    public double ExtentHeight { get; }
    public int ColumnCount => _columns.Length;

    public static MasonryViewportIndex Create(IReadOnlyList<GalleryRow> rows)
    {
        if (rows.Count == 0) return Empty;

        var groups = new Dictionary<int, List<ViewportItem>>();
        var extentWidth = 0d;
        var extentHeight = 0d;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var column = row.ColumnIndex >= 0 ? row.ColumnIndex : 0;
            if (!groups.TryGetValue(column, out var items))
            {
                items = [];
                groups.Add(column, items);
            }

            var top = NormalizeCoordinate(row.PanelY);
            var bottom = Math.Max(top, NormalizeCoordinate(row.PanelBottom));
            items.Add(new ViewportItem(index, top, bottom));
            extentWidth = Math.Max(
                extentWidth,
                NormalizeCoordinate(row.PanelX) + NormalizeCoordinate(row.PanelWidth));
            extentHeight = Math.Max(extentHeight, bottom);
        }

        var columns = groups
            .OrderBy(pair => pair.Key)
            .Select(pair => new ColumnIndex(pair.Key, pair.Value))
            .ToArray();
        return new MasonryViewportIndex(
            rows.Count,
            extentWidth,
            extentHeight,
            columns);
    }

    public IReadOnlyList<int> Query(double top, double bottom)
    {
        if (_columns.Length == 0) return [];
        top = NormalizeCoordinate(top);
        bottom = Math.Max(top, NormalizeCoordinate(bottom));

        var matches = new List<int>();
        foreach (var column in _columns)
        {
            column.AppendIntersecting(top, bottom, matches);
        }

        matches.Sort();
        return matches;
    }

    private static double NormalizeCoordinate(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0;

    private readonly record struct ViewportItem(int OwnerIndex, double Top, double Bottom);

    private sealed class ColumnIndex
    {
        private readonly ViewportItem[] _items;
        private readonly bool _bottomsAreMonotonic;

        public ColumnIndex(int column, List<ViewportItem> items)
        {
            Column = column;
            _items = items
                .OrderBy(item => item.Top)
                .ThenBy(item => item.OwnerIndex)
                .ToArray();
            _bottomsAreMonotonic = true;
            for (var index = 1; index < _items.Length; index++)
            {
                if (_items[index].Bottom + 0.001 >= _items[index - 1].Bottom) continue;
                _bottomsAreMonotonic = false;
                break;
            }
        }

        public int Column { get; }

        public void AppendIntersecting(
            double queryTop,
            double queryBottom,
            List<int> matches)
        {
            if (_items.Length == 0) return;
            if (!_bottomsAreMonotonic)
            {
                foreach (var item in _items)
                {
                    if (item.Top > queryBottom) break;
                    if (item.Bottom >= queryTop) matches.Add(item.OwnerIndex);
                }
                return;
            }

            var first = FindFirstBottomAtOrBelow(queryTop);
            for (var index = first; index < _items.Length; index++)
            {
                var item = _items[index];
                if (item.Top > queryBottom) break;
                if (item.Bottom >= queryTop) matches.Add(item.OwnerIndex);
            }
        }

        private int FindFirstBottomAtOrBelow(double queryTop)
        {
            var low = 0;
            var high = _items.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_items[middle].Bottom >= queryTop)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }
            return low;
        }
    }
}
