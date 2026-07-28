using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Collections.Specialized;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace PromptVault.App;

public sealed class VirtualizingMasonryPanel : VirtualizingPanel, IScrollInfo
{
    private const double CacheViewports = 2;
    private Size _extent;
    private Size _viewport;
    private Point _offset;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || owner.Items.Count == 0)
        {
            RemoveAllGeneratedChildren();
            var emptyViewport = NormalizeViewport(availableSize);
            UpdateScrollInfo(emptyViewport, default);
            return emptyViewport;
        }

        var viewport = NormalizeViewport(availableSize);
        var extent = CalculateExtent(owner, viewport.Width);
        UpdateScrollInfo(viewport, extent);

        var cacheHeight = Math.Max(viewport.Height, 1) * CacheViewports;
        var visibleTop = Math.Max(0, VerticalOffset - cacheHeight);
        var visibleBottom = VerticalOffset + viewport.Height + cacheHeight;
        var firstIndex = FindFirstIntersectingIndex(owner, visibleTop);
        var lastIndex = FindLastIntersectingIndex(owner, firstIndex, visibleBottom);

        if (firstIndex < 0 || lastIndex < firstIndex)
        {
            RemoveAllGeneratedChildren();
            return viewport;
        }

        RemoveChildrenOutsideRange(firstIndex, lastIndex);
        RealizeRange(owner, firstIndex, lastIndex);
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
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
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

    private void RemoveChildrenOutsideRange(int firstIndex, int lastIndex)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var itemIndex = owner?.ItemContainerGenerator.IndexFromContainer(InternalChildren[childIndex]) ?? -1;
            if (itemIndex < 0)
            {
                RemoveInternalChildRange(childIndex, 1);
                continue;
            }
            if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;
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
        if (ItemContainerGenerator is IRecyclingItemContainerGenerator recycling)
        {
            recycling.Recycle(new GeneratorPosition(childIndex, 0), 1);
        }
        else
        {
            ItemContainerGenerator.Remove(new GeneratorPosition(childIndex, 0), 1);
        }
        RemoveInternalChildRange(childIndex, 1);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        _viewport = viewport;
        _extent = extent;
        _offset.X = ClampOffset(_offset.X, extent.Width, viewport.Width);
        _offset.Y = ClampOffset(_offset.Y, extent.Height, viewport.Height);
        ScrollOwner?.InvalidateScrollInfo();
    }

    private static Size CalculateExtent(ItemsControl owner, double viewportWidth)
    {
        var width = Math.Max(0, viewportWidth);
        var height = 0d;
        foreach (var item in owner.Items)
        {
            if (item is not GalleryRow row) continue;
            width = Math.Max(width, row.PanelX + row.PanelWidth);
            height = Math.Max(height, row.PanelBottom);
        }
        return new Size(width, height);
    }

    private static int FindFirstIntersectingIndex(ItemsControl owner, double top)
    {
        for (var index = 0; index < owner.Items.Count; index++)
        {
            if (owner.Items[index] is GalleryRow row && row.PanelBottom >= top)
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindLastIntersectingIndex(
        ItemsControl owner,
        int firstIndex,
        double bottom)
    {
        var last = firstIndex;
        for (var index = Math.Max(0, firstIndex); index < owner.Items.Count; index++)
        {
            if (owner.Items[index] is not GalleryRow row) continue;
            if (row.PanelY > bottom) break;
            last = index;
        }
        return last;
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
}
