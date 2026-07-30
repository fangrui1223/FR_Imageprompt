using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Collections.Specialized;
using PromptVault.App.Services;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace PromptVault.App;

public sealed class VirtualizingMasonryPanel : VirtualizingPanel, IScrollInfo
{
    private const double CacheViewports = 2;
    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private MasonryViewportIndex _layoutIndex = MasonryViewportIndex.Empty;
    private bool _layoutIndexDirty = true;
    private int _visibleItemCount;
    private int _cachedItemCount;
    private double _lastDiagnosticOffset = double.NaN;
    private int _lastRequestedFirstIndex = -1;
    private int _lastRequestedLastIndex = -1;
    private int _lastNewContainerCount;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }
    public int VisibleItemCount => _visibleItemCount;
    public int CachedItemCount => _cachedItemCount;
    public int RealizedItemCount => InternalChildren.Count;

    public IReadOnlyList<int> GetRealizedItemIndices()
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return [];
        return InternalChildren
            .Cast<UIElement>()
            .Select(owner.ItemContainerGenerator.IndexFromContainer)
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToArray();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || owner.Items.Count == 0)
        {
            RemoveAllGeneratedChildren();
            _layoutIndex = MasonryViewportIndex.Empty;
            _layoutIndexDirty = false;
            _visibleItemCount = 0;
            _cachedItemCount = 0;
            var emptyViewport = NormalizeViewport(availableSize);
            UpdateScrollInfo(emptyViewport, default);
            return emptyViewport;
        }

        var viewport = NormalizeViewport(availableSize);
        EnsureLayoutIndex(owner);
        var extent = new Size(
            Math.Max(viewport.Width, _layoutIndex.ExtentWidth),
            _layoutIndex.ExtentHeight);
        UpdateScrollInfo(viewport, extent);

        var cacheHeight = Math.Max(viewport.Height, 1) * CacheViewports;
        var visibleTop = Math.Max(0, VerticalOffset - cacheHeight);
        var visibleBottom = VerticalOffset + viewport.Height + cacheHeight;
        var cachedIndices = _layoutIndex.Query(visibleTop, visibleBottom);
        var viewportIndices = _layoutIndex.Query(
            VerticalOffset,
            VerticalOffset + viewport.Height);
        _visibleItemCount = viewportIndices.Count;
        _cachedItemCount = cachedIndices.Count;

        if (cachedIndices.Count == 0)
        {
            RemoveAllGeneratedChildren();
            return viewport;
        }

        var firstIndex = cachedIndices[0];
        var lastIndex = cachedIndices[^1];
        _lastRequestedFirstIndex = firstIndex;
        _lastRequestedLastIndex = lastIndex;
        _lastNewContainerCount = 0;
        RemoveChildrenOutsideSet(cachedIndices);
        RealizeIndices(owner, cachedIndices);
        TraceViewportDiagnostics(owner, viewportIndices);
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = owner?.ItemContainerGenerator.IndexFromContainer(child) ?? -1;
            if (itemIndex < 0 || owner?.Items[itemIndex] is not GalleryRow row)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            child.Arrange(new Rect(
                row.PanelX - HorizontalOffset,
                row.PanelY - VerticalOffset,
                Math.Max(0, row.PanelWidth),
                Math.Max(0, row.RowHeight)));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(
        object sender,
        ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action == NotifyCollectionChangedAction.Reset && InternalChildren.Count > 0)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
        }
        _layoutIndexDirty = true;
        InvalidateMeasure();
    }

    public void InvalidateLayoutIndex()
    {
        _layoutIndexDirty = true;
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || index < 0 || index >= owner.Items.Count) return;
        if (owner.Items[index] is not GalleryRow row) return;
        BringGeometryIntoView(row.PanelY, row.PanelBottom);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var container = FindDirectChild(visual);
        if (container is null) return rectangle;
        var owner = ItemsControl.GetItemsOwner(this);
        var index = owner?.ItemContainerGenerator.IndexFromContainer(container) ?? -1;
        if (owner is null || index < 0 || owner.Items[index] is not GalleryRow row) return rectangle;
        BringGeometryIntoView(row.PanelY, row.PanelBottom);
        return new Rect(row.PanelX, row.PanelY, row.PanelWidth, row.RowHeight);
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 32);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 32);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 32);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 32);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 96);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 96);
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - 96);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + 96);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);

    public void SetHorizontalOffset(double offset)
    {
        if (!CanHorizontallyScroll) offset = 0;
        offset = ClampOffset(offset, ExtentWidth, ViewportWidth);
        if (Math.Abs(offset - _offset.X) < 0.01) return;
        _offset.X = offset;
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = ClampOffset(offset, ExtentHeight, ViewportHeight);
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    private void RealizeIndices(ItemsControl owner, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0) return;
        var rangeStart = indices[0];
        var previous = rangeStart;
        for (var position = 1; position < indices.Count; position++)
        {
            var current = indices[position];
            if (current == previous + 1)
            {
                previous = current;
                continue;
            }

            RealizeRange(owner, rangeStart, previous);
            rangeStart = current;
            previous = current;
        }
        RealizeRange(owner, rangeStart, previous);
    }

    private void RealizeRange(ItemsControl owner, int firstIndex, int lastIndex)
    {
        var start = ItemContainerGenerator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using var generation = ItemContainerGenerator.StartAt(
            start,
            GeneratorDirection.Forward,
            true);

        for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
        {
            var child = (UIElement)ItemContainerGenerator.GenerateNext(out var newlyRealized);
            if (newlyRealized)
            {
                _lastNewContainerCount++;
                if (childIndex >= InternalChildren.Count)
                {
                    AddInternalChild(child);
                }
                else
                {
                    InsertInternalChild(childIndex, child);
                }

                ItemContainerGenerator.PrepareItemContainer(child);
            }

            if (owner.Items[itemIndex] is GalleryRow row)
            {
                child.Measure(new Size(
                    Math.Max(0, row.PanelWidth),
                    Math.Max(0, row.RowHeight)));
            }
        }
    }

    private void RemoveChildrenOutsideSet(IReadOnlyList<int> indices)
    {
        var retained = indices.ToHashSet();
        var owner = ItemsControl.GetItemsOwner(this);
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var itemIndex = owner?.ItemContainerGenerator.IndexFromContainer(InternalChildren[childIndex]) ?? -1;
            if (itemIndex < 0)
            {
                RemoveInternalChildRange(childIndex, 1);
                continue;
            }
            if (retained.Contains(itemIndex)) continue;
            RemoveGeneratedChild(childIndex);
        }
    }

    private void RemoveAllGeneratedChildren()
    {
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            var itemIndex = owner?.ItemContainerGenerator.IndexFromContainer(InternalChildren[childIndex]) ?? -1;
            if (itemIndex >= 0)
            {
                RemoveGeneratedChild(childIndex);
            }
            else
            {
                RemoveInternalChildRange(childIndex, 1);
            }
        }
    }

    private void RemoveGeneratedChild(int childIndex)
    {
        // RecyclingItemContainerGenerator applies recycled-position offsets
        // that assume a linear stacking panel. With absolute masonry geometry,
        // a non-overlapping scrollbar jump can then shift the requested start
        // by the entire old child count (for example 84 becomes 120), leaving
        // half of the viewport blank. Removing the stale mapping is bounded by
        // the viewport cache and keeps subsequent generation index-correct.
        ItemContainerGenerator.Remove(new GeneratorPosition(childIndex, 0), 1);
        RemoveInternalChildRange(childIndex, 1);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var changed = !AreClose(_viewport.Width, viewport.Width)
                      || !AreClose(_viewport.Height, viewport.Height)
                      || !AreClose(_extent.Width, extent.Width)
                      || !AreClose(_extent.Height, extent.Height);
        _viewport = viewport;
        _extent = extent;
        _offset.X = ClampOffset(_offset.X, extent.Width, viewport.Width);
        _offset.Y = ClampOffset(_offset.Y, extent.Height, viewport.Height);
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    private void EnsureLayoutIndex(ItemsControl owner)
    {
        if (!_layoutIndexDirty && _layoutIndex.ItemCount == owner.Items.Count) return;
        var rows = new GalleryRow[owner.Items.Count];
        for (var index = 0; index < owner.Items.Count; index++)
        {
            if (owner.Items[index] is not GalleryRow row)
            {
                _layoutIndex = MasonryViewportIndex.Empty;
                _layoutIndexDirty = false;
                return;
            }
            rows[index] = row;
        }

        _layoutIndex = MasonryViewportIndex.Create(rows);
        _layoutIndexDirty = false;
    }

    private void TraceViewportDiagnostics(
        ItemsControl owner,
        IReadOnlyList<int> viewportIndices)
    {
        if (!DevelopmentPerformanceTrace.IsEnabled
            || AreClose(_lastDiagnosticOffset, VerticalOffset))
        {
            return;
        }

        _lastDiagnosticOffset = VerticalOffset;
        var mappedIndices = InternalChildren
            .Cast<UIElement>()
            .Select(owner.ItemContainerGenerator.IndexFromContainer)
            .Where(index => index >= 0)
            .ToHashSet();
        var realizedRows = mappedIndices
            .Where(index => index < owner.Items.Count)
            .Count(index => owner.Items[index] is GalleryRow { IsRealized: true });
        DevelopmentPerformanceTrace.Event("masonry-viewport", new
        {
            verticalOffset = Math.Round(VerticalOffset, 3),
            viewportHeight = Math.Round(ViewportHeight, 3),
            extentHeight = Math.Round(ExtentHeight, 3),
            ownerItems = owner.Items.Count,
            expectedVisible = viewportIndices.Count,
            expectedCached = _cachedItemCount,
            requestedFirst = _lastRequestedFirstIndex,
            requestedLast = _lastRequestedLastIndex,
            requestedRange = _lastRequestedLastIndex - _lastRequestedFirstIndex + 1,
            newlyRealized = _lastNewContainerCount,
            realizedChildren = InternalChildren.Count,
            mappedChildren = mappedIndices.Count,
            realizedRows,
            realizedVisible = viewportIndices.Count(mappedIndices.Contains),
            missingVisible = viewportIndices.Count(index => !mappedIndices.Contains(index)),
            visibleIndices = viewportIndices,
            mappedIndices = mappedIndices.OrderBy(index => index).ToArray()
        });
    }

    private Size NormalizeViewport(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : FirstPositive(
                ScrollOwner?.ViewportWidth,
                ScrollOwner?.ActualWidth,
                ActualWidth,
                1200);
        var height = double.IsFinite(availableSize.Height)
            ? Math.Max(0, availableSize.Height)
            : FirstPositive(
                ScrollOwner?.ViewportHeight,
                ScrollOwner?.ActualHeight,
                ActualHeight,
                800);
        return new Size(width, height);
    }

    private static double FirstPositive(params double?[] values)
    {
        foreach (var value in values)
        {
            if (value is > 0 && double.IsFinite(value.Value)) return value.Value;
        }
        return 0;
    }

    private void BringGeometryIntoView(double top, double bottom)
    {
        if (top < VerticalOffset)
        {
            SetVerticalOffset(top);
        }
        else if (bottom > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(bottom - ViewportHeight);
        }
    }

    private UIElement? FindDirectChild(DependencyObject visual)
    {
        var current = visual;
        while (current is not null && !ReferenceEquals(VisualTreeHelper.GetParent(current), this))
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as UIElement;
    }

    private static double ClampOffset(double offset, double extent, double viewport)
    {
        if (!double.IsFinite(offset)) return 0;
        return Math.Clamp(offset, 0, Math.Max(0, extent - viewport));
    }

    private static bool AreClose(double left, double right) =>
        Math.Abs(left - right) < 0.01;
}
