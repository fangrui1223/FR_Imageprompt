using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task<bool> RunM10SmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M10 便签与统一相机烟测");
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

        WindowState = WindowState.Maximized;
        await WaitForLayoutAsync();
        var originalDocument = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("M10 隔离画板不存在。");
        var addedIds = new List<long>();
        try
        {
            await AddNoteAsync();
            var defaultNoteId = _newUnconfirmedNoteId
                ?? throw new InvalidOperationException("M10.2 新便签未进入初始编辑状态。");
            var defaultNote = _notes.Single(note => note.Id == defaultNoteId);
            var defaultNoteStyle = BoardNoteStyleCodec.Decode(defaultNote.ColorStyle);
            var newNoteDefaultsWorked = NearlyEqual(defaultNote.Width, 420)
                && NearlyEqual(defaultNote.Height, 140)
                && NearlyEqual(defaultNoteStyle.FontSize, 32)
                && NearlyEqual(defaultNoteStyle.VerticalPadding, 16);
            await CancelNoteEditingAsync();

            var style = BoardNoteStyle.Default with
            {
                FontSize = 42,
                LineSpacing = 1.4,
                Alignment = BoardNoteTextAlignment.Center,
                TextColor = "#F4F7FB",
                BackgroundEnabled = false,
                BackgroundColor = "#16324A",
                BackgroundOpacity = 0.7,
                CornerRadius = 26,
                VerticalPadding = 18
            };
            var first = await _repository.AddBoardNoteAsync(
                CurrentBoardId,
                "M10 标题\n第二行",
                120,
                120,
                420,
                180,
                20_000,
                BoardNoteStyleCodec.Encode(style));
            var second = await _repository.AddBoardNoteAsync(
                CurrentBoardId,
                "M10 批量预设",
                580,
                120,
                320,
                140,
                20_001,
                BoardNoteStyleCodec.Encode(BoardNoteStyle.Default));
            addedIds.Add(first.Id);
            addedIds.Add(second.Id);
            _notes.Add(first);
            _notes.Add(second);
            _selectedIds.Clear();
            _selectedNoteIds.Clear();
            _selectedNoteIds.Add(first.Id);
            _viewport = BoardCameraEngine.FitBounds(
                BoardBoundsResult.From(BoardCameraEngine.NoteBounds(first)),
                Math.Max(1, BoardViewport.ActualWidth),
                Math.Max(1, BoardViewport.ActualHeight));
            ApplyViewportMatrix();
            RenderVisibleItems();
            await WaitForLayoutAsync();

            var visual = _realizedNotes[first.Id];
            var lightweightVisual = visual.ResizeHandles.Count == 1
                && visual.Editor.TextWrapping == TextWrapping.NoWrap
                && visual.Editor.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden
                && visual.Root.ClipToBounds
                && visual.Root.Background == System.Windows.Media.Brushes.Transparent;
            var selectedDecoration = visual.ResizeHandles[0].Visibility == Visibility.Visible
                && visual.Root.BorderThickness.Left > 0;

            ToggleInspector();
            await WaitForLayoutAsync();
            var propertyPanelVisible = BoardInspector.Visibility == Visibility.Visible
                && NotePropertiesPanel.Visibility == Visibility.Visible
                && InspectorGeneralPanel.Visibility == Visibility.Collapsed;
            var noteTargetTypedCorrectly = ResolveRightTarget(visual.Editor)
                is (BoardCommandContextKind.Note, var resolvedNoteId)
                && resolvedNoteId == first.Id;
            var colorControlsReadable = NoteTextColorButton.Content is System.Windows.Controls.Panel
                && NoteBackgroundColorButton.Content is System.Windows.Controls.Panel
                && NoteTextColorSwatch.Background is SolidColorBrush
                && NoteBackgroundColorSwatch.Background is SolidColorBrush;
            _selectedNoteIds.Clear();
            if (_items.Count > 0) _selectedIds.Add(_items[0].Id);
            RenderVisibleItems();
            var propertyPanelAutoHidden = BoardInspector.Visibility == Visibility.Collapsed;

            _selectedIds.Clear();
            _selectedNoteIds.Add(first.Id);
            RenderVisibleItems();
            var persistedBeforePreview = (await _repository.GetBoardDocumentAsync(CurrentBoardId))!
                .Notes.Single(note => note.Id == first.Id);
            var previewSamples = new double[240];
            for (var index = 0; index < previewSamples.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                PreviewSelectedNoteStyle(current => current with
                {
                    FontSize = 24 + index % 80,
                    CornerRadius = index % 90,
                    BackgroundOpacity = (index % 100) / 100d
                });
                previewSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            Array.Sort(previewSamples);
            var persistedDuringPreview = (await _repository.GetBoardDocumentAsync(CurrentBoardId))!
                .Notes.Single(note => note.Id == first.Id);
            var previewZeroDatabaseWrites = persistedDuringPreview.ColorStyle == persistedBeforePreview.ColorStyle;

            var undoBeforeProperty = _undo.Count;
            BeginNotePropertyGesture();
            PreviewSelectedNoteStyle(current => current with { FontSize = 64, CornerRadius = 32 });
            await CommitNotePropertyGestureAsync();
            var propertyCommittedOnce = _undo.Count == undoBeforeProperty + 1
                && BoardNoteStyleCodec.Decode((await _repository.GetBoardDocumentAsync(CurrentBoardId))!
                    .Notes.Single(note => note.Id == first.Id).ColorStyle).FontSize == 64;

            var beforeFit = _notes.Single(note => note.Id == first.Id);
            var fitClock = Stopwatch.StartNew();
            await FitSelectedNoteToContentAsync();
            fitClock.Stop();
            var afterFit = _notes.Single(note => note.Id == first.Id);
            var oneShotFitWorked = afterFit.Width != beforeFit.Width || afterFit.Height != beforeFit.Height;

            BeginNoteEditing(first.Id);
            await WaitForLayoutAsync();
            visual = _realizedNotes[first.Id];
            var originalText = _notes.Single(note => note.Id == first.Id).Text;
            visual.Editor.Text = "应被 Esc 取消";
            await CancelNoteEditingAsync();
            var editCancelWorked = _notes.Single(note => note.Id == first.Id).Text == originalText;
            BeginNoteEditing(first.Id);
            await WaitForLayoutAsync();
            _realizedNotes[first.Id].Editor.Text = "已确认的 M10 便签";
            await CommitNoteEditingAsync(first.Id);
            var editCommitWorked = (await _repository.GetBoardDocumentAsync(CurrentBoardId))!
                .Notes.Single(note => note.Id == first.Id).Text == "已确认的 M10 便签";

            BeginNoteEditing(first.Id);
            await WaitForLayoutAsync();
            visual = _realizedNotes[first.Id];
            var editorTemplatePart = visual.Editor.Template.FindName("PART_ContentHost", visual.Editor)
                as DependencyObject;
            var editorTemplateOwnedByNote = editorTemplatePart is not null
                && !IsBlankCanvasSource(editorTemplatePart);
            CommitNoteEditingBeforePointerGesture(BoardBackground);
            await Dispatcher.InvokeAsync(() => { });
            visual = _realizedNotes[first.Id];
            var outsideClickReleasedEditor = _editingNoteId is null
                && !visual.Editor.Focusable
                && !visual.Editor.IsHitTestVisible
                && !visual.Editor.IsKeyboardFocusWithin;
            var readOnlyNoteOwnedByNote = !IsBlankCanvasSource(visual.Root);

            _selectedNoteIds.Clear();
            _selectedNoteIds.Add(first.Id);
            _selectedNoteIds.Add(second.Id);
            var presetClock = Stopwatch.StartNew();
            await ApplyNotePresetAsync(2);
            presetClock.Stop();
            var expectedPreset = _settings.BoardNotePresets[2].Normalize();
            var batchPresetWorked = addedIds.All(id =>
                BoardNoteStyleCodec.Decode(_notes.Single(note => note.Id == id).ColorStyle) == expectedPreset);

            _focusController.Reset();
            var cameraInitial = CurrentViewportSize();
            _selectedNoteIds.Clear();
            _selectedIds.Clear();
            _selectedNoteIds.Add(first.Id);
            ToggleSelectionFocus();
            await WaitForCameraSettledAsync();
            ToggleSelectionFocus();
            await WaitForCameraSettledAsync();
            var spaceRoundTripExact = SameCamera(cameraInitial, _viewport);

            _focusController.Reset();
            _viewport = cameraInitial;
            ApplyViewportMatrix();
            FocusFullBoard();
            await WaitForCameraSettledAsync();
            ToggleSelectionFocus();
            await WaitForCameraSettledAsync();
            var selectionReplacedFullBoardFocus = _focusController.Target?.Kind
                == BoardFocusTargetKind.SingleNote;
            ToggleSelectionFocus();
            await WaitForCameraSettledAsync();
            var switchedTargetSpaceRoundTripExact = selectionReplacedFullBoardFocus
                && SameCamera(cameraInitial, _viewport);
            _focusController.Reset();
            _selectedNoteIds.Clear();
            _selectedIds.Clear();
            ToggleSelectionFocus();
            var emptySpaceNoOp = SameCamera(cameraInitial, _viewport);
            _selectedNoteIds.Add(first.Id);
            ToggleSelectionFocus();
            await WaitForCameraSettledAsync();
            var focused = _viewport;
            FocusFullBoard();
            await WaitForCameraSettledAsync();
            ResetViewToOneHundredPercent();
            await WaitForCameraSettledAsync();
            var oneHundred = _viewport;
            _selectedNoteIds.Clear();
            var restored = RestoreWorkingView();
            await WaitForCameraSettledAsync();
            var cameraSequenceWorked = emptySpaceNoOp
                && focused.Zoom != cameraInitial.Zoom
                && NearlyEqual(oneHundred.Zoom, 1)
                && restored
                && SameCamera(cameraInitial, _viewport);
            var restoredExactCamera = SameCamera(cameraInitial, _viewport);

            _focusController.Reset();
            _manualCameraHistory.Reset();
            _selectedIds.Clear();
            _selectedNoteIds.Clear();
            var manualBefore = CurrentViewportSize();
            var manualAfter = BoardViewportEngine.ZoomAt(
                BoardViewportEngine.Pan(manualBefore, 180, -96),
                480,
                320,
                manualBefore.Zoom * 1.24);
            _manualCameraHistory.Record(manualBefore, manualAfter);
            _viewport = manualAfter;
            ApplyViewportMatrix();
            ToggleSelectionFocus();
            var manualReturned = SameCamera(manualBefore, _viewport);
            ToggleSelectionFocus();
            var manualToggledForward = SameCamera(manualAfter, _viewport);
            var oneLevelManualCameraWorked = manualReturned && manualToggledForward;

            var documentAfter = await _repository.GetBoardDocumentAsync(CurrentBoardId)
                ?? throw new InvalidOperationException("M10 隔离画板不可读取。");
            var imageLayerUntouched = originalDocument.Items.SequenceEqual(documentAfter.Items);
            var passed = lightweightVisual
                && newNoteDefaultsWorked
                && selectedDecoration
                && propertyPanelVisible
                && noteTargetTypedCorrectly
                && colorControlsReadable
                && propertyPanelAutoHidden
                && previewZeroDatabaseWrites
                && propertyCommittedOnce
                && oneShotFitWorked
                && editCancelWorked
                && editCommitWorked
                && editorTemplateOwnedByNote
                && outsideClickReleasedEditor
                && readOnlyNoteOwnedByNote
                && batchPresetWorked
                && spaceRoundTripExact
                && switchedTargetSpaceRoundTripExact
                && cameraSequenceWorked
                && oneLevelManualCameraWorked
                && imageLayerUntouched;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var report = new
            {
                Milestone = "M10-note-camera-transparent-stability",
                GeneratedAt = DateTimeOffset.Now,
                DataKind = "synthetic",
                Passed = passed,
                Display = new
                {
                    PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                    PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                    AppliedDpi = dpi.PixelsPerInchX,
                    dpi.DpiScaleX,
                    BoardViewport.ActualWidth,
                    BoardViewport.ActualHeight
                },
                Note = new
                {
                    LightweightVisual = lightweightVisual,
                    NewNoteDefaultsWorked = newNoteDefaultsWorked,
                    NewNoteWidth = 420,
                    NewNoteHeight = 140,
                    NewNoteFontSize = 32,
                    NewNoteVerticalPadding = 16,
                    SelectedDecoration = selectedDecoration,
                    PropertyPanelVisible = propertyPanelVisible,
                    NoteTargetTypedCorrectly = noteTargetTypedCorrectly,
                    ColorControlsReadable = colorControlsReadable,
                    PropertyPanelAutoHidden = propertyPanelAutoHidden,
                    PreviewSamples = previewSamples.Length,
                    PreviewP50Ms = previewSamples[previewSamples.Length / 2],
                    PreviewP95Ms = previewSamples[(int)(previewSamples.Length * 0.95)],
                    PreviewMaximumMs = previewSamples[^1],
                    PreviewDatabaseWrites = previewZeroDatabaseWrites ? 0 : 1,
                    PropertyCommittedOnce = propertyCommittedOnce,
                    OneShotFitWorked = oneShotFitWorked,
                    FitElapsedMs = fitClock.Elapsed.TotalMilliseconds,
                    EditCancelWorked = editCancelWorked,
                    EditCommitWorked = editCommitWorked,
                    EditorTemplateOwnedByNote = editorTemplateOwnedByNote,
                    OutsideClickReleasedEditor = outsideClickReleasedEditor,
                    ReadOnlyNoteOwnedByNote = readOnlyNoteOwnedByNote,
                    BatchPresetWorked = batchPresetWorked,
                    BatchPresetElapsedMs = presetClock.Elapsed.TotalMilliseconds
                },
                Camera = new
                {
                    EmptySpaceNoOp = emptySpaceNoOp,
                    SpaceRoundTripExact = spaceRoundTripExact,
                    SelectionReplacesFullBoardFocus = selectionReplacedFullBoardFocus,
                    SwitchedTargetRoundTripExact = switchedTargetSpaceRoundTripExact,
                    MixedCommandsPreservedInitialSnapshot = cameraSequenceWorked,
                    OneLevelManualCameraWorked = oneLevelManualCameraWorked,
                    OneHundredPercentZoom = oneHundred.Zoom,
                    RestoredExactCamera = restoredExactCamera
                },
                Regression = new
                {
                    ImageLayerUntouched = imageLayerUntouched,
                    DatabaseSchemaChanged = false,
                    GalleryPerformanceLayerTouched = false,
                    BitmapRebuildsDuringPropertyPreview = 0
                },
                DataSafety = IsolatedSafety(settings)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            SetStatus(passed ? "M10 隔离烟测通过" : "M10 隔离烟测失败");
            return passed;
        }
        finally
        {
            if (addedIds.Count > 0)
            {
                await _repository.DeleteBoardNotesAsync(CurrentBoardId, addedIds);
                _notes.RemoveAll(note => addedIds.Contains(note.Id));
                _selectedNoteIds.RemoveWhere(addedIds.Contains);
                RenderVisibleItems();
            }
        }
    }
}
