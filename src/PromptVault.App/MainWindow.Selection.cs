using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private long? _selectionAnchorId;
    private long? _selectionFocusId;
    private bool _galleryKeyboardMode;

    private bool HandleGallerySelectionKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _inspectorVisible)
        {
            CloseInspectorClick(this, new RoutedEventArgs());
            return true;
        }

        var focused = Keyboard.FocusedElement;
        if (!_galleryKeyboardMode
            && focused is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
        {
            return false;
        }
        if (focused is System.Windows.Controls.Primitives.ButtonBase) return false;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C)
        {
            CopyCurrentSelectionPrompts();
            return true;
        }

        if (e.Key == Key.Space)
        {
            var id = CurrentSelectionId();
            if (id is not null) ShowImmersiveViewer(id.Value);
            return id is not null;
        }

        var direction = e.Key switch
        {
            Key.Left => GalleryNavigationDirection.Left,
            Key.Right => GalleryNavigationDirection.Right,
            Key.Up => GalleryNavigationDirection.Up,
            Key.Down => GalleryNavigationDirection.Down,
            Key.Home => GalleryNavigationDirection.Home,
            Key.End => GalleryNavigationDirection.End,
            _ => (GalleryNavigationDirection?)null
        };
        if (direction is null) return false;

        var positions = new List<GalleryCardPosition>();
        var rowTop = 0d;
        foreach (var row in Rows)
        {
            positions.AddRange(row.LayoutItems.Select(item => new GalleryCardPosition(
                item.Item.Id,
                item.LayoutX,
                rowTop + item.LayoutY,
                item.LayoutWidth,
                item.ImageHeight)));
            rowTop += row.RowHeight + row.RowMargin.Bottom;
        }
        var target = GallerySelectionNavigator.Move(
            positions,
            CurrentSelectionId(),
            direction.Value);
        if (target is null) return false;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectRangeTo(target.Value, additive: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
        else
        {
            SelectSingle(target.Value);
        }
        ScrollSelectionIntoView(target.Value);
        return true;
    }

    private void HandleCardSelection(GalleryCardViewModel card)
    {
        _galleryKeyboardMode = true;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectRangeTo(card.Id, additive: modifiers.HasFlag(ModifierKeys.Control));
        }
        else if (_multiSelectMode || modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleCardSelection(card.Id);
        }
        else
        {
            SelectSingle(card.Id);
        }
    }

    private void FocusGalleryInput() => _galleryKeyboardMode = true;

    private void NotePointerFocusTarget(DependencyObject? source)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null
            || FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null
            || FindAncestor<PasswordBox>(source) is not null)
        {
            _galleryKeyboardMode = false;
        }
    }

    private void SelectSingle(long itemId)
    {
        _selectedItemIds.Clear();
        _selectedItemIds.Add(itemId);
        _selectionAnchorId = itemId;
        _selectionFocusId = itemId;
        ApplySelectionState();
        InspectItem(itemId);
    }

    private void ToggleCardSelection(long itemId)
    {
        if (!_selectedItemIds.Add(itemId)) _selectedItemIds.Remove(itemId);
        _selectionFocusId = itemId;
        if (_selectionAnchorId is null || _selectedItemIds.Count <= 1) _selectionAnchorId = itemId;
        ApplySelectionState();
        InspectItem(itemId);
    }

    private void SelectRangeTo(long targetId, bool additive)
    {
        var orderedIds = _items.Select(item => item.Id).ToArray();
        var anchor = _selectionAnchorId ?? _selectionFocusId ?? targetId;
        var range = GallerySelectionNavigator.Range(orderedIds, anchor, targetId);
        if (!additive) _selectedItemIds.Clear();
        foreach (var id in range) _selectedItemIds.Add(id);
        _selectionAnchorId = anchor;
        _selectionFocusId = targetId;
        _multiSelectMode = _selectedItemIds.Count > 1 || _multiSelectMode;
        ApplySelectionState();
        InspectItem(targetId);
    }

    private void ApplySelectionState()
    {
        foreach (var row in Rows)
        {
            foreach (var card in row.Items)
            {
                card.IsSelected = _selectedItemIds.Contains(card.Id);
            }
        }
        UpdateSelectionVisual();
    }

    private long? CurrentSelectionId()
    {
        if (_selectionFocusId is { } focus && _items.Any(item => item.Id == focus)) return focus;
        var selected = _items.FirstOrDefault(item => _selectedItemIds.Contains(item.Id));
        if (selected is not null) return selected.Id;
        if (_inspectedItem is { } inspected && _items.Any(item => item.Id == inspected.Id)) return inspected.Id;
        return _items.FirstOrDefault()?.Id;
    }

    private void InspectItem(long itemId)
    {
        var card = Rows.SelectMany(row => row.Items).FirstOrDefault(candidate => candidate.Id == itemId);
        if (card is not null)
        {
            OpenInspector(card);
            return;
        }

        var item = _items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is not null) OpenInspector(item, thumbnail: null);
    }

    private void ScrollSelectionIntoView(long itemId)
    {
        var row = Rows.FirstOrDefault(candidate => candidate.LayoutItems.Any(item => item.Item.Id == itemId));
        if (row is null) return;
        RowsList.ScrollIntoView(row);
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var card = row.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (card is not null) OpenInspector(card);
        }));
    }

    private void CopyCurrentSelectionPrompts()
    {
        var selected = _items
            .Where(item => _selectedItemIds.Contains(item.Id))
            .Select(item => item.Prompt)
            .ToArray();
        if (selected.Length == 0 && _inspectedItem is not null) selected = [_inspectedItem.Prompt];
        CopyPromptsToClipboard(selected);
    }

    private void CopyPromptsToClipboard(IEnumerable<string> prompts)
    {
        var values = prompts.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (values.Length == 0)
        {
            ToastService.Show(this, "当前图片没有提示词");
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine, values));
            ToastService.Show(this, values.Length > 1 ? $"已复制 {values.Length} 条提示词" : "提示词已复制");
        }
        catch (Exception ex)
        {
            AppLog.Warning("clipboard-copy", "Selected prompts could not be copied.", ex);
            ToastService.Show(this, $"复制失败：{ex.Message}");
        }
    }

    private void CardAuxiliaryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        CopyPromptsToClipboard([card.Item.Prompt]);
        e.Handled = true;
    }
}
