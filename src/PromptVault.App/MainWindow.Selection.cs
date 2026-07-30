using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private long? _selectionAnchorId;
    private long? _selectionFocusId;
    private bool _galleryShortcutContext;
    private DispatcherTimer? _transparentSelectionFadeTimer;
    private DispatcherTimer? _transparentSelectionAnimationTimer;
    private DateTimeOffset _transparentSelectionAnimationStartedAt;

    private bool HandleGallerySelectionKey(KeyEventArgs e)
    {
        var shortcutKey = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        if (shortcutKey == Key.Escape && _inspectorVisible)
        {
            CloseInspectorClick(this, new RoutedEventArgs());
            return true;
        }

        var focused = Keyboard.FocusedElement;
        if (!_galleryShortcutContext
            && focused is (System.Windows.Controls.Primitives.TextBoxBase
                or PasswordBox
                or System.Windows.Controls.ComboBox))
        {
            return false;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && shortcutKey == Key.C)
        {
            _ = CopyCurrentSelectionPromptsAsync();
            return true;
        }

        if (shortcutKey == Key.Space)
        {
            var id = CurrentSelectionId();
            if (id is not null) ShowImmersiveViewer(id.Value);
            return id is not null;
        }

        if (shortcutKey == Key.Q && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_transparentMode)
            {
                ShowTransparentInspectorNotice();
                return true;
            }
            if (_inspectorVisible)
            {
                CloseInspectorClick(this, new RoutedEventArgs());
                return true;
            }
            var id = CurrentSelectionId();
            if (id is not null) InspectItem(id.Value);
            return id is not null;
        }

        var direction = shortcutKey switch
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
        foreach (var row in Rows)
        {
            positions.AddRange(row.LayoutItems.Select(item => new GalleryCardPosition(
                item.Item.Id,
                row.PanelX + item.LayoutX,
                row.PanelY + item.LayoutY,
                item.LayoutWidth,
                item.ImageHeight)));
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

    private void FocusGalleryInput()
    {
        _galleryShortcutContext = true;
        Keyboard.ClearFocus();
        GalleryKeyboardFocusTarget.Focus();
        Keyboard.Focus(GalleryKeyboardFocusTarget);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(
                FocusManager.GetFocusScope(GalleryKeyboardFocusTarget),
                GalleryKeyboardFocusTarget);
            GalleryKeyboardFocusTarget.Focus();
            Keyboard.Focus(GalleryKeyboardFocusTarget);
        });
    }

    private void NotePointerFocusTarget(DependencyObject? source)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null
            || FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null
            || FindAncestor<PasswordBox>(source) is not null
            || FindAncestor<System.Windows.Controls.ComboBox>(source) is not null)
        {
            _galleryShortcutContext = false;
            return;
        }
    }

    private void SelectSingle(long itemId)
    {
        _selectedItemIds.Clear();
        _selectedItemIds.Add(itemId);
        _selectionAnchorId = itemId;
        _selectionFocusId = itemId;
        ApplySelectionState();
        if (_inspectorVisible) InspectItem(itemId);
    }

    private void ToggleCardSelection(long itemId)
    {
        if (!_selectedItemIds.Add(itemId)) _selectedItemIds.Remove(itemId);
        _selectionFocusId = itemId;
        if (_selectionAnchorId is null || _selectedItemIds.Count <= 1) _selectionAnchorId = itemId;
        ApplySelectionState();
        if (_inspectorVisible) InspectItem(itemId);
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
        if (_inspectorVisible) InspectItem(targetId);
    }

    private void ApplySelectionState()
    {
        foreach (var row in Rows)
        {
            foreach (var card in row.Items)
            {
                card.IsSelected = _selectedItemIds.Contains(card.Id);
                if (card.IsSelected) card.SelectionVisualStrength = _transparentMode ? 0.72 : 1;
            }
        }
        if (_transparentMode && _selectedItemIds.Count > 0) QueueTransparentSelectionFade();
        UpdateSelectionVisual();
    }

    private void QueueTransparentSelectionFade()
    {
        _transparentSelectionFadeTimer ??= CreateTransparentSelectionFadeTimer();
        _transparentSelectionAnimationTimer?.Stop();
        _transparentSelectionFadeTimer.Stop();
        _transparentSelectionFadeTimer.Start();
    }

    private DispatcherTimer CreateTransparentSelectionFadeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_settings.ReducedMotionEnabled)
            {
                SetTransparentSelectionStrength(0);
                return;
            }
            StartTransparentSelectionFadeAnimation();
        };
        return timer;
    }

    private void StartTransparentSelectionFadeAnimation()
    {
        _transparentSelectionAnimationStartedAt = DateTimeOffset.UtcNow;
        _transparentSelectionAnimationTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _transparentSelectionAnimationTimer.Stop();
        _transparentSelectionAnimationTimer.Tick -= TransparentSelectionAnimationTick;
        _transparentSelectionAnimationTimer.Tick += TransparentSelectionAnimationTick;
        _transparentSelectionAnimationTimer.Start();
    }

    private void TransparentSelectionAnimationTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer) return;
        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _transparentSelectionAnimationStartedAt).TotalMilliseconds / 180d,
            0,
            1);
        SetTransparentSelectionStrength(0.72 * (1 - progress));
        if (progress < 1) return;
        timer.Stop();
    }

    private void SetTransparentSelectionStrength(double strength)
    {
            foreach (var card in Rows.SelectMany(row => row.Items).Where(card => card.IsSelected))
            {
                card.SelectionVisualStrength = strength;
            }
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
            if (!_inspectorVisible) return;
            var card = row.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (card is not null) OpenInspector(card);
        }));
    }

    private async Task CopyCurrentSelectionPromptsAsync()
    {
        var selectedEntries = _items
            .Where(item => _selectedItemIds.Contains(item.Id))
            .ToArray();
        if (selectedEntries.Length == 0 && _inspectedItem is not null)
        {
            selectedEntries = [_inspectedItem];
        }
        var hydrated = await EnsureFullEntriesAsync(selectedEntries);
        CopyPromptsToClipboard(hydrated.Select(item => item.Prompt));
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

    private async void CardAuxiliaryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        var item = await EnsureFullEntryAsync(card.Item);
        if (item is not null) CopyPromptsToClipboard([item.Prompt]);
        e.Handled = true;
    }
}
