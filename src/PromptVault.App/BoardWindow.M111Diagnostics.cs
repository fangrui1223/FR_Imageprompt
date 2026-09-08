using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunM111SmokeAsync(string reportPath, AppSettings settings, Dictionary<string, bool> result)
    {
        EnsureIsolatedM8Settings(settings, "M11.1 画板体验烟测");
        if (!_repository.Paths.Root.Contains($"{Path.DirectorySeparatorChar}m11-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("M11.1 仅使用独立 M11 合成图库。");
        await EnsureBoardLoadedAsync();
        WindowState = WindowState.Maximized;
        Activate();
        await WaitForLayoutAsync();
        var original = SnapshotScene();
        var cursor = System.Windows.Forms.Cursor.Position;
        var note = await _repository.AddBoardNoteAsync(CurrentBoardId, "M11.1 共同选择与参考锁定", 170, 490, 420, 140);
        var sample = original.Items.Take(3).Select((item, index) => item with
        {
            X = 100 + index * 400, Y = 100 + index * 80, Width = 240 + index * 20,
            Height = 250, Rotation = index == 1 ? 15 : 0
        }).ToArray();
        if (sample.Length != 3) throw new InvalidOperationException("M11.1 需要三张合成图片。");
        try
        {
            await RestoreSnapshotAsync(new BoardSceneSnapshot(sample, [note]), "M11.1 合成场景");
            _viewport = new BoardViewport(0, 0, 1, BoardViewport.ActualWidth, BoardViewport.ActualHeight);
            ApplyViewportMatrix();
            SelectAllBoardContent();
            await WaitForLayoutAsync();
            var first = sample[0];
            result["selectAllIncludesImagesAndNotes"] = _selectedIds.Count == 3 && _selectedNoteIds.SetEquals([note.Id]);
            result["mixedSelectionOutlines"] = SelectionBoundsOverlay.Visibility == Visibility.Visible
                && _realizedNotes[note.Id].Root.BorderThickness.Left > 0
                && ((System.Windows.Controls.Border)_realized[first.Id]).BorderThickness.Left > 0;
            BoardItemMouseLeftButtonDown(_realized[first.Id], M111MouseEvent(MouseButton.Left));
            result["imagePointerRetainsNotes"] = _selectedIds.Count == 3 && _selectedNoteIds.SetEquals([note.Id]);
            var beforeMove = SnapshotScene();
            var beforeMoveDb = await _repository.GetBoardDocumentAsync(CurrentBoardId);
            var history = _undo.Count;
            ApplySelectionTranslation(51, -23);
            var duringMoveDb = await _repository.GetBoardDocumentAsync(CurrentBoardId);
            result["mixedMovePreviewNoWrites"] = beforeMoveDb!.Items.SequenceEqual(duringMoveDb!.Items)
                && beforeMoveDb.Notes.SequenceEqual(duringMoveDb.Notes);
            result["mixedMoveIncludesBothKinds"] = _items.All(item => item.X == beforeMove.Items.Single(x => x.Id == item.Id).X + 51)
                && _notes.Single().Y == note.Y - 23;
            await CompleteItemPointerGestureAsync(_realized[first.Id], M111MouseEvent(MouseButton.Left));
            result["mixedMoveOneUndo"] = _undo.Count == history + 1;
            await ExecuteBoardCommandAsync(BoardCommandId.Undo);
            result["mixedMoveUndoRestoresBothKinds"] = beforeMove.Items.SequenceEqual(_items) && beforeMove.Notes.SequenceEqual(_notes);
            SelectAllBoardContent();
            NoteRootMouseLeftButtonDown(_realizedNotes[note.Id].Root, M111MouseEvent(MouseButton.Left));
            result["notePointerRetainsImages"] = _selectedIds.Count == 3 && _selectedNoteIds.SetEquals([note.Id]);
            ApplySelectionTranslation(-14, 32);
            CancelSelectionTranslation();
            _dragNoteId = null;
            _noteGestureSnapshot = null;
            Mouse.Capture(null);
            result["canceledMixedMoveRestoresBothKinds"] = beforeMove.Items.SequenceEqual(_items) && beforeMove.Notes.SequenceEqual(_notes);
            history = _undo.Count;
            await M11SqlAsync("CREATE TRIGGER m111_fail_delete BEFORE DELETE ON board_notes BEGIN SELECT RAISE(ABORT, 'M111 mixed delete failure'); END;");
            try
            {
                await ExecuteBoardCommandAsync(BoardCommandId.RemoveSelection);
                var retained = await _repository.GetBoardDocumentAsync(CurrentBoardId);
                result["failedMixedDeleteRollsBackBothKinds"] = retained!.Items.Count == 3 && retained.Notes.Count == 1
                    && beforeMove.Items.SequenceEqual(_items) && beforeMove.Notes.SequenceEqual(_notes) && _undo.Count == history;
            }
            finally { await M11SqlAsync("DROP TRIGGER IF EXISTS m111_fail_delete;"); }
            await ExecuteBoardCommandAsync(BoardCommandId.RemoveSelection);
            result["mixedDeleteAndOneUndo"] = _items.Count == 0 && _notes.Count == 0 && _undo.Count == history + 1;
            await ExecuteBoardCommandAsync(BoardCommandId.Undo);
            result["mixedDeleteUndoRestoresBothKinds"] = beforeMove.Items.SequenceEqual(_items) && beforeMove.Notes.SequenceEqual(_notes);

            BeginNoteEditing(note.Id);
            await WaitForLayoutAsync();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.A)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            BoardWindowPreviewKeyDown(this, key);
            result["textEditorKeepsKeyboardOwnership"] = IsTextEditingFocus() && !key.Handled && _selectedIds.Count == 0;
            await CommitNoteEditingAsync(note.Id);
            SelectAllBoardContent();

            HideBoardTopBar();
            ShowTopBarFromKeyboard();
            ScheduleTopBarHide();
            ShowTopBarFromKeyboard(isRepeat: true);
            await Task.Delay(650);
            BoardTopHideTimerTick(null, EventArgs.Empty);
            result["keyboardOpenSurvivesTimerAndRepeat"] = _boardTopBarShown && _boardTopBarKeyboardHeld && !_boardTopHideTimer.IsEnabled;
            BoardWindowPreviewMouseDown(this, M111MouseEvent(MouseButton.Left, BoardViewport));
            result["canvasClickClosesKeyboardBar"] = !_boardTopBarShown && !_boardTopBarKeyboardHeld;
            ShowTopBarFromKeyboard();
            ShowTopBarFromKeyboard();
            result["keyboardToggleClearsTimers"] = !_boardTopBarShown && !_boardTopHideTimer.IsEnabled && !_boardTopRevealTimer.IsEnabled;

            // Exercise actual pointer entry/leave and the production DispatcherTimer.
            await M111MovePointerAsync(new Point(500, 180), 120);
            await M111MovePointerAsync(new Point(500, 4), 120);
            result["fastTopCrossingDoesNotReveal"] = !_boardTopBarShown;
            await M111MovePointerAsync(new Point(500, 180), 550);
            result["leavingSensorCancelsReveal"] = !_boardTopBarShown && !_boardTopRevealTimer.IsEnabled;
            await M111MovePointerAsync(new Point(500, 4), 650);
            result["continuous450msHoverReveals"] = _boardTopBarShown && !_boardTopBarKeyboardHeld;
            await M111MovePointerAsync(new Point(500, 180), 650);
            result["pointerOpenedBarHidesAfterLeaving"] = !_boardTopBarShown;

            WindowState = WindowState.Normal;
            Width = 1200;
            Height = 760;
            await WaitForLayoutAsync();
            await M111MovePointerAsync(new Point(500, 180), 120);
            await M111MovePointerAsync(new Point(500, 4), 650);
            result["normalWindowResizeEdgeHoverReveals"] = _boardTopBarShown;
            await M111MovePointerAsync(new Point(500, 180), 650);
            result["normalWindowPointerHide"] = !_boardTopBarShown;
            WindowState = WindowState.Maximized;
            await WaitForLayoutAsync();

            SelectAllBoardContent();
            var beforeLock = SnapshotScene();
            StartPan(new Point(300, 300));
            await ExecuteBoardCommandAsync(BoardCommandId.ToggleReferenceLock);
            result["lockingEndsActivePanWithoutStuckCapture"] = _panStart is null && Mouse.Captured is null;
            result["referenceLockEnabledAndRemembered"] = _referenceLocked
                && AppSettings.Load(settings.StorageFilePath).ReferenceLockedBoards.Contains(ReferenceLockKey)
                && ReferenceLockBadge.Visibility == Visibility.Visible;
            await ExecuteBoardCommandAsync(BoardCommandId.RemoveSelection);
            await ExecuteBoardCommandAsync(BoardCommandId.AlignImagesLeft);
            await ExecuteBoardCommandAsync(BoardCommandId.Undo);
            await AddCollectionItemsAsync([new BoardAddItem(first.AssetId!.Value, 100, 100)]);
            BeginNoteEditing(note.Id);
            BeginCropMode();
            TransformHandleDragStarted(TopLeftHandle, new DragStartedEventArgs(0, 0));
            NoteResizeStarted(_realizedNotes[note.Id].ResizeHandles[0], new DragStartedEventArgs(0, 0));
            BoardItemMouseLeftButtonDown(_realized[first.Id], M111MouseEvent(MouseButton.Left));
            NoteRootMouseLeftButtonDown(_realizedNotes[note.Id].Root, M111MouseEvent(MouseButton.Left));
            result["lockBlocksCommandsAndPointerEditing"] = beforeLock.Items.SequenceEqual(_items) && beforeLock.Notes.SequenceEqual(_notes)
                && _dragItemId is null && _dragNoteId is null && !_imageTransformGestureActive
                && _noteResizeOrigin is null && _editingNoteId is null && !_cropModeActive;
            var cameraBefore = _viewport;
            BoardViewportMouseWheel(BoardViewport, new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            result["lockAllowsZoom"] = _viewport.Zoom != cameraBefore.Zoom;
            StartPan(new Point(300, 300));
            result["lockAllowsPan"] = _panStart is not null;
            EndPan();
            await LoadBoardAsync(CurrentBoardId);
            result["lockSurvivesBoardReload"] = _referenceLocked && ReferenceLockButton.IsChecked == true;
            var reopened = new BoardWindow(_repository, new BoardWorkspaceService(_repository, settings), settings, CurrentBoardId);
            try
            {
                reopened.Show();
                await reopened.EnsureBoardLoadedAsync();
                await WaitForLayoutAsync();
                result["lockSurvivesNewWindow"] = reopened._referenceLocked && reopened.ReferenceLockButton.IsChecked == true;
            }
            finally { reopened.ApproveApplicationExit(); reopened.Close(); Activate(); }
            var another = await _repository.CreateBoardAsync("M11.1 锁定窗口隔离");
            var anotherWindow = await _workspace.OpenAsync(this, another.Id);
            try
            {
                await anotherWindow.EnsureBoardLoadedAsync();
                await anotherWindow.LoadBoardAsync(CurrentBoardId);
                result["boardSwitchCannotCreateUnlockedDuplicate"] = anotherWindow.CurrentBoardId == another.Id;
            }
            finally
            {
                anotherWindow.ApproveApplicationExit();
                anotherWindow.Close();
                await _repository.DeleteBoardAsync(another.Id);
                Activate();
            }
            await ExecuteBoardCommandAsync(BoardCommandId.ToggleReferenceLock);
            result["unlockRestoresEditing"] = !_referenceLocked && NotePropertiesPanel.IsEnabled;

            SelectAllBoardContent();
            var beforeSpacing = SnapshotScene();
            history = _undo.Count;
            await ExecuteBoardCommandAsync(BoardCommandId.DistributeImagesHorizontally);
            var spacing = _items.Select(BoardCameraEngine.ItemBounds).OrderBy(b => b.X).ToArray();
            result["equalSpacingCommandAndOneUndo"] = Math.Abs(spacing[1].X - spacing[0].X - spacing[0].Width
                - (spacing[2].X - spacing[1].X - spacing[1].Width)) < .000001 && _undo.Count == history + 1;
            await ExecuteBoardCommandAsync(BoardCommandId.Undo);
            result["equalSpacingUndoRestoresNotesAndImages"] = beforeSpacing.Items.SequenceEqual(_items) && beforeSpacing.Notes.SequenceEqual(_notes);
            var beforeLayout = SnapshotScene();
            history = _undo.Count;
            await ExecuteBoardCommandAsync(BoardCommandId.AlignImagesLeft);
            var afterLayout = SnapshotScene();
            result["layoutAlignsRotatedImagesOnly"] = _items.Select(BoardCameraEngine.ItemBounds).All(b => Math.Abs(b.X - BoardCameraEngine.ItemBounds(_items[0]).X) < .000001)
                && beforeLayout.Notes.SequenceEqual(_notes);
            result["layoutOneUndo"] = _undo.Count == history + 1;
            await M11SqlAsync("CREATE TRIGGER m111_no_write BEFORE UPDATE ON board_items BEGIN SELECT RAISE(ABORT, 'M111 no-op must not write'); END;");
            try
            {
                await ExecuteBoardCommandAsync(BoardCommandId.AlignImagesLeft);
                result["layoutNoOpNoHistoryOrWrites"] = _undo.Count == history + 1 && !_pendingSaves.HasPending;
            }
            finally { await M11SqlAsync("DROP TRIGGER IF EXISTS m111_no_write;"); }
            await ExecuteBoardCommandAsync(BoardCommandId.Undo);
            result["layoutUndoRestoresExactly"] = beforeLayout.Items.SequenceEqual(_items) && beforeLayout.Notes.SequenceEqual(_notes);
            await ExecuteBoardCommandAsync(BoardCommandId.Redo);
            result["layoutRedoRestoresExactly"] = afterLayout.Items.SequenceEqual(_items) && afterLayout.Notes.SequenceEqual(_notes);
            await M11SqlAsync("CREATE TRIGGER m111_fail_layout BEFORE UPDATE ON board_items BEGIN SELECT RAISE(ABORT, 'M111 layout failure'); END;");
            try
            {
                await ExecuteBoardCommandAsync(BoardCommandId.AlignImagesBottom);
                result["layoutFailureRetainsPendingAndError"] = _pendingSaves.HasPending && _saveError is not null;
                await ExecuteBoardCommandAsync(BoardCommandId.ToggleReferenceLock);
                result["failedSaveDoesNotClaimLock"] = !_referenceLocked;
                ReferenceLockButton.IsChecked = true;
                ReferenceLockClick(ReferenceLockButton, new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitForLayoutAsync();
                result["failedLockRestoresToggleVisual"] = ReferenceLockButton.IsChecked == false && !_referenceLocked;
            }
            finally { await M11SqlAsync("DROP TRIGGER IF EXISTS m111_fail_layout;"); }
            await RetryBoardSaveAsync();
            var stored = await _repository.GetBoardItemsAsync(CurrentBoardId);
            result["layoutRetryPersistsCoordinates"] = !_pendingSaves.HasPending && _items.All(item => stored.Any(s => s.Id == item.Id && s.X == item.X && s.Y == item.Y));
            var menu = BuildContextMenu(BoardCommandContextKind.MultiSelection);
            result["layoutAndLockMenuEntriesPresent"] = DescendantMenuItems(menu).Any(item => item.Tag is BoardCommandId.AlignImagesLeft)
                && DescendantMenuItems(menu).Any(item => item.Tag is BoardCommandId.ToggleReferenceLock);
            HideBoardTopBar();
            ShowTopBarFromKeyboard();
            await ExecuteBoardCommandAsync(BoardCommandId.ToggleReferenceLock);
            await WaitForLayoutAsync();
            result["toolbarLabelsFit"] = FindVisualChildren<ButtonBase>(BoardTopBar)
                .All(button => ToolbarTextFits(button, VisualTreeHelper.GetDpi(this).PixelsPerDip));
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.ChangeExtension(reportPath, ".png"))) encoder.Save(file);
            await ExecuteBoardCommandAsync(BoardCommandId.ToggleReferenceLock);
        }
        finally
        {
            System.Windows.Forms.Cursor.Position = cursor;
            _referenceLocked = false;
            _settings.ReferenceLockedBoards.Remove(ReferenceLockKey);
            _settings.Save();
            await RestoreSnapshotAsync(original, "M11.1 合成场景已还原");
        }
    }

    private static MouseButtonEventArgs M111MouseEvent(MouseButton button, object? source = null) =>
        new(Mouse.PrimaryDevice, 0, button) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = source };

    private async Task M111MovePointerAsync(Point point, int waitMs)
    {
        var screen = PointToScreen(point);
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)screen.X, (int)screen.Y);
        await Task.Delay(waitMs);
        await WaitForLayoutAsync();
    }
}
