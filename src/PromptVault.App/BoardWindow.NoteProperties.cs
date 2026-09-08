using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private bool _updatingNotePropertyControls;
    private BoardSceneSnapshot? _notePropertyGestureSnapshot;
    private long? _notePropertyGestureNoteId;
    private BoardSceneSnapshot? _noteColorSnapshot;
    private long? _noteColorNoteId;
    private bool _noteColorTargetsBackground;
    private bool _noteColorCancelOnClose;
    private bool _updatingNoteColorHex;

    private void UpdateNotePropertiesPanel()
    {
        var noteId = SingleSelectedNoteId;
        NotePropertiesPanel.Visibility = noteId is not null ? Visibility.Visible : Visibility.Collapsed;
        InspectorGeneralPanel.Visibility = _selectedNoteIds.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (noteId is null) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
        if (note is null) return;
        var style = BoardNoteStyleCodec.Decode(note.ColorStyle);
        _updatingNotePropertyControls = true;
        NoteFontSizeSlider.Value = style.FontSize;
        NoteLineSpacingSlider.Value = style.LineSpacing;
        NoteAlignmentSelector.SelectedValue = style.Alignment.ToString();
        NoteBackgroundEnabledCheckBox.IsChecked = style.BackgroundEnabled;
        NoteBackgroundOpacitySlider.Value = style.BackgroundOpacity;
        NoteCornerRadiusSlider.Value = style.CornerRadius;
        NoteVerticalPaddingSlider.Value = style.VerticalPadding;
        NoteTextColorSwatch.Background = new SolidColorBrush(ParseColor(style.TextColor));
        NoteBackgroundColorSwatch.Background = new SolidColorBrush(ParseColor(style.BackgroundColor));
        UpdateNotePropertyLabels(style);
        _updatingNotePropertyControls = false;
    }

    private void UpdateNotePropertyLabels(BoardNoteStyle style)
    {
        NoteFontSizeLabel.Text = $"字号 {style.FontSize:0}";
        NoteLineSpacingLabel.Text = $"行距 {style.LineSpacing:0.0}";
        NoteBackgroundOpacityLabel.Text = $"背景透明度 {style.BackgroundOpacity:P0}";
        NoteCornerRadiusLabel.Text = $"圆角 {style.CornerRadius:0}";
        NoteVerticalPaddingLabel.Text = $"垂直边距 {style.VerticalPadding:0}";
    }

    private void NotePropertySliderPointerDown(object sender, MouseButtonEventArgs e)
    {
        BeginNotePropertyGesture();
    }

    private async void NotePropertySliderPointerUp(object sender, MouseButtonEventArgs e)
    {
        await CommitNotePropertyGestureAsync();
    }

    private async void NotePropertySliderKeyUp(object sender, KeyEventArgs e)
    {
        await CommitNotePropertyGestureAsync();
    }

    private void NotePropertySliderChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingNotePropertyControls || SingleSelectedNoteId is null) return;
        BeginNotePropertyGesture();
        PreviewSelectedNoteStyle(style => style with
        {
            FontSize = NoteFontSizeSlider.Value,
            LineSpacing = NoteLineSpacingSlider.Value,
            BackgroundOpacity = NoteBackgroundOpacitySlider.Value,
            CornerRadius = NoteCornerRadiusSlider.Value,
            VerticalPadding = NoteVerticalPaddingSlider.Value
        });
    }

    private void BeginNotePropertyGesture()
    {
        if (_referenceLocked) return;
        if (SingleSelectedNoteId is not { } noteId) return;
        if (_notePropertyGestureNoteId == noteId && _notePropertyGestureSnapshot is not null) return;
        _notePropertyGestureSnapshot = SnapshotScene();
        _notePropertyGestureNoteId = noteId;
    }

    private void PreviewSelectedNoteStyle(Func<BoardNoteStyle, BoardNoteStyle> transform)
    {
        if (_referenceLocked) return;
        if (SingleSelectedNoteId is not { } noteId) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
        if (note is null) return;
        var style = transform(BoardNoteStyleCodec.Decode(note.ColorStyle)).Normalize();
        note = note with { ColorStyle = BoardNoteStyleCodec.Encode(style) };
        ReplaceNote(note);
        if (_realizedNotes.TryGetValue(noteId, out var visual)) UpdateNoteVisual(visual, note);
        UpdateNotePropertyLabels(style);
    }

    private async Task CommitNotePropertyGestureAsync()
    {
        if (_notePropertyGestureSnapshot is not { } before
            || _notePropertyGestureNoteId is not { } noteId) return;
        _notePropertyGestureSnapshot = null;
        _notePropertyGestureNoteId = null;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
        if (note is null) return;
        var original = before.Notes.SingleOrDefault(candidate => candidate.Id == noteId);
        if (original is null || original.ColorStyle == note.ColorStyle) return;
        CommitSceneHistorySnapshot(before);
        await SaveNoteAsync(note, "便签样式已保存");
    }

    private async void NoteAlignmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingNotePropertyControls
            || NoteAlignmentSelector.SelectedValue is not string value
            || !Enum.TryParse<BoardNoteTextAlignment>(value, out var alignment)) return;
        await AlignSelectedNotesAsync(alignment);
    }

    private async void NoteBackgroundEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingNotePropertyControls || NoteBackgroundEnabledCheckBox.IsChecked is not { } enabled) return;
        await ApplyStyleToSelectedNotesAsync(
            style => style with { BackgroundEnabled = enabled },
            "便签背景已保存");
    }

    private void NoteTextColorClick(object sender, RoutedEventArgs e) =>
        OpenNoteColorPopup(background: false, NoteTextColorButton);

    private void NoteBackgroundColorClick(object sender, RoutedEventArgs e) =>
        OpenNoteColorPopup(background: true, NoteBackgroundColorButton);

    private void OpenNoteColorPopup(bool background, FrameworkElement placementTarget)
    {
        if (_referenceLocked) return;
        if (SingleSelectedNoteId is not { } noteId) return;
        var note = _notes.Single(candidate => candidate.Id == noteId);
        var style = BoardNoteStyleCodec.Decode(note.ColorStyle);
        var value = background ? style.BackgroundColor : style.TextColor;
        _noteColorSnapshot = SnapshotScene();
        _noteColorNoteId = noteId;
        _noteColorTargetsBackground = background;
        _noteColorCancelOnClose = false;
        NoteColorPopupTitle.Text = background ? "背景颜色" : "文字颜色";
        NoteColorPopup.PlacementTarget = placementTarget;
        _updatingNoteColorHex = true;
        NoteColorHexBox.Text = value;
        NoteColorPreviewSwatch.Background = new SolidColorBrush(ParseColor(value));
        _updatingNoteColorHex = false;
        NoteColorPopup.IsOpen = true;
        NoteColorHexBox.Focus();
        NoteColorHexBox.SelectAll();
    }

    private void NoteColorPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;
        _updatingNoteColorHex = true;
        NoteColorHexBox.Text = value;
        _updatingNoteColorHex = false;
        PreviewNoteColor(value);
    }

    private void NoteColorHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingNoteColorHex || !NoteColorPopup.IsOpen) return;
        if (TryNormalizeHexColor(NoteColorHexBox.Text, out var value)) PreviewNoteColor(value);
    }

    private void PreviewNoteColor(string value)
    {
        NoteColorPreviewSwatch.Background = new SolidColorBrush(ParseColor(value));
        PreviewSelectedNoteStyle(style => _noteColorTargetsBackground
            ? style with { BackgroundColor = value }
            : style with { TextColor = value });
    }

    private void NoteColorHexPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _noteColorCancelOnClose = true;
            NoteColorPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            NoteColorPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private async void NoteColorPopupClosed(object? sender, EventArgs e)
    {
        var before = _noteColorSnapshot;
        var noteId = _noteColorNoteId;
        var canceled = _noteColorCancelOnClose;
        _noteColorSnapshot = null;
        _noteColorNoteId = null;
        _noteColorCancelOnClose = false;
        if (before is null || noteId is null) return;
        var original = before.Notes.SingleOrDefault(candidate => candidate.Id == noteId.Value);
        var current = _notes.SingleOrDefault(candidate => candidate.Id == noteId.Value);
        if (original is null || current is null) return;
        if (canceled)
        {
            ReplaceNote(original);
            if (_realizedNotes.TryGetValue(original.Id, out var visual)) UpdateNoteVisual(visual, original);
            UpdateNotePropertiesPanel();
            Keyboard.Focus(BoardViewport);
            return;
        }
        if (original.ColorStyle != current.ColorStyle)
        {
            CommitSceneHistorySnapshot(before);
            await SaveNoteAsync(
                current,
                _noteColorTargetsBackground ? "便签背景颜色已保存" : "便签文字颜色已保存");
        }
        UpdateNotePropertiesPanel();
        Keyboard.Focus(BoardViewport);
    }

    private static bool TryNormalizeHexColor(string? input, out string value)
    {
        value = input?.Trim().ToUpperInvariant() ?? string.Empty;
        return value.Length == 7
            && value[0] == '#'
            && value.Skip(1).All(Uri.IsHexDigit);
    }

    private async void EditNoteClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelectedNoteId is { } noteId) BeginNoteEditing(noteId);
        await Task.CompletedTask;
    }

    private async void FitNoteContentClick(object sender, RoutedEventArgs e) =>
        await FitSelectedNoteToContentAsync();

    private async void NotePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var slot))
            await ApplyNotePresetAsync(slot);
        UpdateNotePropertiesPanel();
    }

    private async void NotePresetRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var slot))
        {
            e.Handled = true;
            await SaveNotePresetAsync(slot);
        }
    }
}
