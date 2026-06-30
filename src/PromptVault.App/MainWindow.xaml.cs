using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using System.Windows.Threading;
using PromptVault.App.Services;
using PromptVault.Core;
using Forms = System.Windows.Forms;

namespace PromptVault.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    private readonly LibraryRepository _repository;
    private readonly CaptureCoordinator _capture;
    private readonly AppSettings _settings;
    private readonly ClipboardMonitor _clipboard;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly DispatcherTimer _subtleStatusTimer;
    private readonly Dictionary<long, CancellationTokenSource> _clicks = new();
    private readonly HashSet<long> _selectedItemIds = new();
    private IReadOnlyList<CategoryRecord> _categories = [];
    private IReadOnlyList<GalleryEntry> _items = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _showTrash;
    private bool _allowClose;
    private bool _suppressFilterRefresh;
    private bool _suppressExternalRefresh;
    private bool _multiSelectMode;
    private bool _ignoreNextCardClick;
    private double _layoutWidth;
    private readonly bool _trueTransparentWindow;
    private readonly MainWindowSnapshot? _initialSnapshot;
    private string? _externalFolderId;
    private GalleryCardViewModel? _dragCandidate;
    private Point _dragStart;
    private ScrollViewer? _rowsScrollViewer;
    private const double GalleryWheelPixelsPerNotch = 180d;

    public ObservableCollection<GalleryRow> Rows { get; } = new();

    public MainWindow(LibraryRepository repository, CaptureCoordinator capture, AppSettings settings, bool transparentWindow = false, MainWindowSnapshot? initialSnapshot = null)
    {
        _trueTransparentWindow = transparentWindow;
        _transparentMode = transparentWindow;
        _initialSnapshot = initialSnapshot;
        if (transparentWindow)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
        }

        _repository = repository;
        _capture = capture;
        _settings = settings;
        DataContext = this;
        InitializeComponent();
        CategoryList.ContextMenu = new System.Windows.Controls.ContextMenu();
        ExternalFolderList.ContextMenu = new System.Windows.Controls.ContextMenu();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshAsync(); };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); RegroupIfNeeded(); };
        _subtleStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _subtleStatusTimer.Tick += (_, _) => { _subtleStatusTimer.Stop(); UpdateBaseStatus(); };
        _clipboard = new ClipboardMonitor(this, capture, () => _categories, SaveCaptureAsync);
        _clipboard.SetEnabled(_settings.CaptureListeningEnabled);
        UpdateCaptureToggleVisual();
        UpdateSelectionVisual();
        Loaded += async (_, _) =>
        {
            await LoadCategoriesAsync();
            LoadExternalFolders();
            ApplyInitialSnapshot();
            ApplyTransparentMode();
            await RefreshAsync();
            RestoreViewerFromSnapshot();
        };
        SizeChanged += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
    }

    private bool IsExternalMode => _externalFolderId is not null;

    internal MainWindowSnapshot CreateSnapshot()
    {
        var categoryId = (CategoryList.SelectedItem as CategoryChoice)?.Id;
        long? viewerItemId = _viewerIndex >= 0 && _viewerIndex < _items.Count ? _items[_viewerIndex].Id : null;
        return new MainWindowSnapshot(
            Left,
            Top,
            Width,
            Height,
            WindowState,
            Topmost,
            SearchBox.Text,
            TagBox.Text,
            categoryId,
            _externalFolderId,
            _showTrash,
            _oldestFirst,
            _multiSelectMode,
            _selectedItemIds.ToArray(),
            viewerItemId);
    }

    private void ApplyInitialSnapshot()
    {
        if (_initialSnapshot is not { } snapshot) return;
        if (snapshot.Width > 0) Width = snapshot.Width;
        if (snapshot.Height > 0) Height = snapshot.Height;
        if (!double.IsNaN(snapshot.Left)) Left = snapshot.Left;
        if (!double.IsNaN(snapshot.Top)) Top = snapshot.Top;
        WindowState = snapshot.WindowState;
        Topmost = snapshot.Topmost;
        SearchBox.Text = snapshot.SearchText;
        TagBox.Text = snapshot.TagText;
        _showTrash = snapshot.ShowTrash;
        _oldestFirst = snapshot.OldestFirst;
        _multiSelectMode = snapshot.MultiSelectMode;
        _externalFolderId = snapshot.ExternalFolderId;
        _selectedItemIds.Clear();
        foreach (var id in snapshot.SelectedItemIds) _selectedItemIds.Add(id);

        if (_externalFolderId is not null && ExternalFolderList.ItemsSource is IEnumerable<ExternalFolderChoice> externalChoices)
        {
            var external = externalChoices.FirstOrDefault(x => x.Id == _externalFolderId);
            if (external is not null)
            {
                _suppressExternalRefresh = true;
                ExternalFolderList.SelectedItem = external;
                _suppressExternalRefresh = false;
                _suppressFilterRefresh = true;
                CategoryList.SelectedIndex = -1;
                _suppressFilterRefresh = false;
            }
        }
        else if (CategoryList.ItemsSource is IEnumerable<CategoryChoice> choices)
        {
            var match = choices.FirstOrDefault(x => x.Id == snapshot.CategoryId);
            if (match is not null)
            {
                _suppressFilterRefresh = true;
                CategoryList.SelectedItem = match;
                _suppressFilterRefresh = false;
            }
        }

        UpdatePinVisual();
        UpdateTrashVisual();
        UpdateSelectionVisual();
    }

    private void RestoreViewerFromSnapshot()
    {
        if (_initialSnapshot?.ViewerItemId is long itemId) ShowImmersiveViewer(itemId);
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            ToastService.Show(this, _clipboard.IsEnabled ? "FR_Imageprompt \u5DF2\u5728\u6258\u76D8\u7EE7\u7EED\u76D1\u542C" : "FR_Imageprompt \u5DF2\u5728\u6258\u76D8\u7EE7\u7EED\u8FD0\u884C");
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clipboard.Dispose();
        base.OnClosed(e);
    }

    private async Task LoadCategoriesAsync()
    {
        _categories = await _repository.GetCategoriesAsync();
        var choices = new List<CategoryChoice> { new(null, "\u5168\u90E8\u56FE\u7247") };
        choices.Add(new CategoryChoice(0, "\u672A\u5206\u7C7B"));
        choices.AddRange(_categories.Select(x => new CategoryChoice(x.Id, x.Name)));
        CategoryList.ItemsSource = choices;
        _suppressFilterRefresh = true;
        CategoryList.SelectedIndex = 0;
        _suppressFilterRefresh = false;
    }

    private void LoadExternalFolders()
    {
        var choices = _settings.ExternalFolders
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new ExternalFolderChoice(x.Id, string.IsNullOrWhiteSpace(x.Name) ? System.IO.Path.GetFileName(x.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) : x.Name, x.Path))
            .ToArray();
        ExternalFolderList.ItemsSource = choices;
    }

    private async Task RefreshAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        try
        {
            StatusText.Text = "濠殿喗绻愮徊钘夛耿椤忓牆绀夐柣妯煎劋缁?..";
            IReadOnlyList<GalleryEntry> result;
            if (IsExternalMode)
            {
                var folder = _settings.ExternalFolders.FirstOrDefault(x => x.Id == _externalFolderId);
                result = folder is null ? [] : await LoadExternalFolderAsync(folder, token);
            }
            else
            {
                var selected = CategoryList.SelectedItem as CategoryChoice;
                long? category = selected?.Id is > 0 ? selected.Id : null;
                var libraryItems = await _repository.SearchAsync(new SearchOptions(
                    SearchBox.Text, category, TagBox.Text, _showTrash, _oldestFirst, 5000), token);
                if (selected?.Id == 0) libraryItems = libraryItems.Where(x => x.CategoryId is null).ToArray();
                result = libraryItems.Select(GalleryEntry.FromLibrary).ToArray();
            }

            _items = result;
            _selectedItemIds.RemoveWhere(id => _items.All(item => item.Id != id));
            BuildRows();
            CountText.Text = IsExternalMode ? $"\u5916\u90E8\u6587\u4EF6\u5939 - {_items.Count}" : (_showTrash ? $"\u56DE\u6536\u7AD9 - {_items.Count}" : $"\u56FE\u7247\u6536\u85CF - {_items.Count}");
            UpdateBaseStatus();
            UpdateTrashVisual();
            UpdateSelectionVisual();
        }
        catch (OperationCanceledException) { }
            catch (Exception ex) { StatusText.Text = $"\u52A0\u8F7D\u5931\u8D25\uFF1A{ex.Message}"; }
    }

    private Task<IReadOnlyList<GalleryEntry>> LoadExternalFolderAsync(ExternalFolderSetting folder, CancellationToken cancellationToken)
    {
        var query = SearchBox.Text.Trim();
        var oldestFirst = _oldestFirst;
        return Task.Run<IReadOnlyList<GalleryEntry>>(() =>
        {
            if (!Directory.Exists(folder.Path)) return [];
            var files = Directory.EnumerateFiles(folder.Path)
                .Where(path => SupportedImageExtensions.Contains(System.IO.Path.GetExtension(path)))
                .Where(path => query.Length == 0 || System.IO.Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase));
            files = oldestFirst
                ? files.OrderBy(File.GetLastWriteTimeUtc)
                : files.OrderByDescending(File.GetLastWriteTimeUtc);

        var items = new List<GalleryEntry>();
        foreach (var file in files.Take(5000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (width, height) = ReadImageSize(file);
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                items.Add(new GalleryEntry(
                    ExternalId(file),
                    "external",
                    file,
                    file,
                    width,
                    height,
                    System.IO.Path.GetExtension(file).TrimStart('.').ToLowerInvariant(),
                    name,
                    $"\u6765\u6E90\u6587\u4EF6\u5939\uFF1A{folder.Path}",
                    null,
                    name,
                    "",
                    new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero),
                    null,
                    true,
                    folder.Id));
            }
            catch { }
        }
            return items;
        }, cancellationToken);
    }

    private static (int Width, int Height) ReadImageSize(string path)
    {
        var frame = ImagePipeline.DecodeFirstFrame(path);
        return (Math.Max(1, frame.PixelWidth), Math.Max(1, frame.PixelHeight));
    }

    private static long ExternalId(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToLowerInvariant()));
        var value = BitConverter.ToInt64(bytes, 0);
        if (value == long.MinValue) value = long.MaxValue;
        value = Math.Abs(value);
        return value == 0 ? -1 : -value;
    }

    private void BuildRows()
    {
        Rows.Clear();
        if (_items.Count == 0) return;

        var availableWidth = Math.Max(300, ActualWidth - 56);
        const double targetImageHeight = 210;
        const double horizontalMargin = 14;
        var pending = new List<GalleryEntry>();
        var ratioSum = 0d;

        foreach (var item in _items)
        {
            pending.Add(item);
            ratioSum += LayoutRatio(item);
            var projectedWidth = ratioSum * targetImageHeight + pending.Count * horizontalMargin;
            if (projectedWidth < availableWidth && pending.Count < 7) continue;
            AddGalleryRow(pending, ratioSum, availableWidth, true);
            pending.Clear();
            ratioSum = 0;
        }

        if (pending.Count > 0) AddGalleryRow(pending, ratioSum, availableWidth, false);
    }

    private void AddGalleryRow(IReadOnlyList<GalleryEntry> items, double ratioSum, double availableWidth, bool fill)
    {
        const double horizontalMargin = 14;
        var imageHeight = fill
            ? Math.Clamp((availableWidth - items.Count * horizontalMargin) / ratioSum, 140, 270)
            : 210;
        var row = new GalleryRow();
        foreach (var item in items)
        {
            var width = Math.Max(92, LayoutRatio(item) * imageHeight);
            row.Items.Add(new GalleryCardViewModel(item, _repository.Paths, width, imageHeight, _selectedItemIds.Contains(item.Id)));
        }
        Rows.Add(row);
    }

    private static double LayoutRatio(GalleryEntry item)
    {
        var ratio = item.Height <= 0 ? 1d : item.Width / (double)item.Height;
        return Math.Clamp(ratio, 0.52, 2.5);
    }

    private void RegroupIfNeeded()
    {
        var width = Math.Max(300, ActualWidth - 56);
        if (Math.Abs(width - _layoutWidth) < 32) return;
        _layoutWidth = width;
        BuildRows();
    }

    private async Task SaveCaptureAsync(PendingCapture pending, string prompt, string notes, long? category, string tags)
    {
        var result = await _capture.SaveAsync(pending, prompt, notes, category, [tags]);
        await Dispatcher.InvokeAsync(async () =>
        {
            ToastService.Show(this, result.WasDuplicate ? "\u5DF2\u66F4\u65B0\u539F\u6709\u6536\u85CF" : "\u56FE\u7247\u4E0E\u63D0\u793A\u8BCD\u5DF2\u4FDD\u5B58");
            await RefreshAsync();
        });
    }


    private void RowsListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        if (_rowsScrollViewer is null) return;

        var notches = e.Delta / 120d;
        var target = _rowsScrollViewer.VerticalOffset - notches * GalleryWheelPixelsPerNotch;
        target = Math.Clamp(target, 0, _rowsScrollViewer.ScrollableHeight);
        _rowsScrollViewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { _searchTimer.Stop(); _searchTimer.Start(); }

    private async void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterRefresh) return;
        if (CategoryList.SelectedItem is null) return;
        if (_externalFolderId is not null)
        {
            _externalFolderId = null;
            _suppressExternalRefresh = true;
            ExternalFolderList.SelectedIndex = -1;
            _suppressExternalRefresh = false;
        }
        if (_showTrash)
        {
            _showTrash = false;
            FinishSelectionOperation();
            UpdateTrashVisual();
        }

        FinishSelectionOperation();
        await RefreshAsync();
    }

    private async void ExternalFolderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressExternalRefresh) return;
        if (ExternalFolderList.SelectedItem is not ExternalFolderChoice choice) return;
        _externalFolderId = choice.Id;
        _showTrash = false;
        _suppressFilterRefresh = true;
        CategoryList.SelectedIndex = -1;
        _suppressFilterRefresh = false;
        FinishSelectionOperation();
        await RefreshAsync();
    }

    private async void TrashClick(object sender, RoutedEventArgs e)
    {
        _showTrash = !_showTrash;
        _externalFolderId = null;
        FinishSelectionOperation();
        _suppressExternalRefresh = true;
        ExternalFolderList.SelectedIndex = -1;
        _suppressExternalRefresh = false;
        if (_showTrash && CategoryList.SelectedIndex != 0)
        {
            _suppressFilterRefresh = true;
            CategoryList.SelectedIndex = 0;
            _suppressFilterRefresh = false;
        }

        UpdateTrashVisual();
        await RefreshAsync();
    }

    private void CaptureToggleClick(object sender, RoutedEventArgs e)
    {
        _settings.CaptureListeningEnabled = !_clipboard.IsEnabled;
        _settings.Save();
        _clipboard.SetEnabled(_settings.CaptureListeningEnabled);
        UpdateCaptureToggleVisual();
        UpdateBaseStatus();
        ToastService.Show(this, _clipboard.IsEnabled ? "\u6536\u5F55\u76D1\u542C\u5DF2\u5F00\u542F" : "\u6536\u5F55\u76D1\u542C\u5DF2\u5173\u95ED");
    }

    private void MultiSelectClick(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = !_multiSelectMode;
        if (!_multiSelectMode) ClearSelection();
        UpdateSelectionVisual();
    }

    private async void DeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (IsExternalMode)
        {
            ShowSubtleStatus("\u5916\u90E8\u6587\u4EF6\u5939\u4E0D\u4F1A\u5220\u9664\u6E90\u6587\u4EF6\uFF0C\u53EF\u53F3\u952E\u6536\u85CF\u5230\u56FE\u5E93");
            return;
        }

        if (_selectedItemIds.Count == 0)
        {
            ToastService.Show(this, "\u5148\u9009\u62E9\u8981\u5904\u7406\u7684\u56FE\u7247");
            return;
        }

        await ApplyTrashActionAsync(_selectedItemIds.ToArray());
    }

    private async void AddCategoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new CategoryDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await _repository.AddCategoryAsync(dialog.CategoryName, dialog.Description); await LoadCategoriesAsync(); }
        catch (Exception ex) { ToastService.Show(this, ex.Message); }
    }

    private async void AddExternalFolderClick(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog { Description = "\u9009\u62E9\u8981\u6D4F\u89C8\u7684\u56FE\u7247\u6587\u4EF6\u5939", UseDescriptionForTitle = true };
        if (picker.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
        var path = System.IO.Path.GetFullPath(picker.SelectedPath);
        var existing = _settings.ExternalFolders.FirstOrDefault(x => string.Equals(System.IO.Path.GetFullPath(x.Path), path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ExternalFolderSetting
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                Path = path,
                AddedAt = DateTimeOffset.UtcNow
            };
            _settings.ExternalFolders.Add(existing);
            _settings.Save();
            LoadExternalFolders();
        }

        if (ExternalFolderList.ItemsSource is IEnumerable<ExternalFolderChoice> choices)
        {
            ExternalFolderList.SelectedItem = choices.FirstOrDefault(x => x.Id == existing.Id);
        }
        _externalFolderId = existing.Id;
        await RefreshAsync();
    }

    private async void ImportModelClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "\u9009\u62E9 FR_Imageprompt AI \u6A21\u578B\u5305", Filter = "\u6A21\u578B\u5305 (*.zip)|*.zip" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            await using var stream = File.OpenRead(picker.FileName);
            var hash = await ContentHasher.Sha256Async(stream);
            await new ModelPackInstaller().VerifyAndInstallAsync(picker.FileName, hash, _repository.Paths.Models);
            ToastService.Show(this, "\u6A21\u578B\u5305\u5DF2\u5BFC\u5165");
        }
        catch (Exception ex) { ToastService.Show(this, $"\u6A21\u578B\u5305\u5BFC\u5165\u5931\u8D25\uFF1A{ex.Message}"); }
    }

    private void OpenLibraryClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo
    { FileName = _repository.Paths.Root, UseShellExecute = true });

    private async void CardImageLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GalleryCardViewModel card) await card.LoadAsync();
    }

    private void CardMouseEnter(object sender, MouseEventArgs e) => AnimateScale(sender as Border, 1.02);
    private void CardMouseLeave(object sender, MouseEventArgs e) => AnimateScale(sender as Border, 1.0);
    private static void AnimateScale(Border? border, double to)
    {
        if (border?.RenderTransform is not ScaleTransform transform) return;
        if (transform.IsFrozen) { transform = transform.Clone(); border.RenderTransform = transform; }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void CardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            CancelClick(card.Id);
            ShowImmersiveViewer(card.Id);
            e.Handled = true;
            return;
        }

        _dragCandidate = card;
        _dragStart = e.GetPosition(this);
    }

    private void CardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < 8 && Math.Abs(point.Y - _dragStart.Y) < 8) return;
        StartCardDrag(sender as DependencyObject, _dragCandidate);
    }

    private void StartCardDrag(DependencyObject? source, GalleryCardViewModel card)
    {
        if (source is null) return;
        var paths = GetOperationTargetEntries(card)
            .Select(ResolveOriginalPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _dragCandidate = null;
        if (paths.Length == 0) return;
        CancelClick(card.Id);
        _ignoreNextCardClick = true;
        var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, paths);
        System.Windows.DragDrop.DoDragDrop(source, data, System.Windows.DragDropEffects.Copy);
    }

    private async void CardClick(object sender, MouseButtonEventArgs e)
    {
        if (_ignoreNextCardClick)
        {
            _ignoreNextCardClick = false;
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        if (e.ClickCount > 1) { CancelClick(card.Id); return; }
        CancelClick(card.Id);

        if (_multiSelectMode || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _multiSelectMode = true;
            ToggleCardSelection(card);
            return;
        }

        var cts = new CancellationTokenSource();
        _clicks[card.Id] = cts;
        try
        {
            await Task.Delay(System.Windows.Forms.SystemInformation.DoubleClickTime + 30, cts.Token);
            Clipboard.SetText(card.Item.Prompt);
            ToastService.Show(this, "\u63D0\u793A\u8BCD\u5DF2\u590D\u5236");
        }
        catch (OperationCanceledException) { }
        finally { _clicks.Remove(card.Id); cts.Dispose(); }
    }

    private void ToggleCardSelection(GalleryCardViewModel card)
    {
        if (!_selectedItemIds.Add(card.Id)) _selectedItemIds.Remove(card.Id);
        card.IsSelected = _selectedItemIds.Contains(card.Id);
        UpdateSelectionVisual();
    }

    private void ClearSelection()
    {
        _selectedItemIds.Clear();
        foreach (var row in Rows) foreach (var card in row.Items) card.IsSelected = false;
        UpdateSelectionVisual();
    }

    private void FinishSelectionOperation()
    {
        _multiSelectMode = false;
        ClearSelection();
    }

    private void UpdateSelectionVisual()
    {
        if (MultiSelectButton is null || DeleteSelectedButton is null) return;
        MultiSelectButton.Content = _multiSelectMode ? "\u9000\u51FA\u591A\u9009" : "\u591A\u9009";
        MultiSelectButton.Background = _multiSelectMode ? new SolidColorBrush(Color.FromRgb(62, 118, 153)) : new SolidColorBrush(Color.FromRgb(29, 42, 59));
        DeleteSelectedButton.Content = _showTrash ? $"\u6062\u590D\u9009\u4E2D{SelectedSuffix()}" : $"\u5220\u9664\u9009\u4E2D{SelectedSuffix()}";
        DeleteSelectedButton.IsEnabled = !IsExternalMode && _selectedItemIds.Count > 0;
    }

    private string SelectedSuffix() => _selectedItemIds.Count > 0 ? $" - {_selectedItemIds.Count}" : "";

    private void UpdateTrashVisual()
    {
        if (TrashButton is null) return;
        TrashButton.Content = _showTrash ? "\u9000\u51FA\u56DE\u6536\u7AD9" : "\u56DE\u6536\u7AD9";
        TrashButton.Background = _showTrash ? new SolidColorBrush(Color.FromRgb(62, 118, 153)) : new SolidColorBrush(Color.FromRgb(29, 42, 59));
        if (DeleteSelectedButton is not null) DeleteSelectedButton.Content = _showTrash ? $"\u6062\u590D\u9009\u4E2D{SelectedSuffix()}" : $"\u5220\u9664\u9009\u4E2D{SelectedSuffix()}";
    }

    private void UpdateCaptureToggleVisual()
    {
        if (CaptureToggleButton is null) return;
        CaptureToggleButton.Content = _clipboard.IsEnabled ? "\u6536\u5F55\u76D1\u542C\uFF1A\u5F00" : "\u6536\u5F55\u76D1\u542C\uFF1A\u5173";
        CaptureToggleButton.Background = _clipboard.IsEnabled ? new SolidColorBrush(Color.FromRgb(29, 42, 59)) : new SolidColorBrush(Color.FromRgb(49, 58, 72));
        CaptureToggleButton.BorderBrush = _clipboard.IsEnabled ? new SolidColorBrush(Color.FromRgb(86, 214, 255)) : new SolidColorBrush(Color.FromRgb(73, 88, 106));
    }

    private void UpdateBaseStatus()
    {
        if (StatusText is null) return;
        StatusText.Text = IsExternalMode
            ? "\u5916\u90E8\u6587\u4EF6\u5939\uFF1A\u53EF\u62D6\u51FA\u56FE\u7247\uFF0C\u53F3\u952E\u6216 Alt+M \u6536\u85CF\u5230\u56FE\u5E93"
            : (_showTrash ? "\u8BB0\u5F55\u5C06\u5728\u79FB\u5165\u56DE\u6536\u7AD9 30 \u5929\u540E\u81EA\u52A8\u6E05\u7406" : (_clipboard.IsEnabled ? "\u590D\u5236\u6216\u62D6\u5165\u4E00\u5F20\u56FE\u7247\u5373\u53EF\u5F00\u59CB\u6536\u5F55" : "\u6536\u5F55\u76D1\u542C\u5DF2\u5173\u95ED\uFF0C\u53EF\u6B63\u5E38\u6D4F\u89C8\u56FE\u7247\u4E0E\u590D\u5236\u63D0\u793A\u8BCD"));
    }

    private void ShowSubtleStatus(string message)
    {
        StatusText.Text = message;
        _subtleStatusTimer.Stop();
        _subtleStatusTimer.Start();
    }

    private void CancelClick(long id) { if (_clicks.Remove(id, out var cts)) cts.Cancel(); }

    private void CardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border { ContextMenu: { } menu } border || border.DataContext is not GalleryCardViewModel card)
        {
            e.Handled = true;
            return;
        }

        var targets = GetOperationTargetEntries(card).ToArray();
        if (targets.Length == 0)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        menu.Background = new SolidColorBrush(Color.FromArgb(245, 17, 28, 42));
        menu.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        if (targets.Length > 1)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = $"\u5DF2\u9009 {targets.Length} \u5F20", IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        if (targets.Any(x => x.IsExternal))
        {
            var collect = new System.Windows.Controls.MenuItem { Header = "\u6536\u85CF\u5230\u56FE\u5E93", Tag = new ExternalCollect(targets, null) };
            collect.Click += CollectExternalClick;
            menu.Items.Add(collect);
            var collectTo = new System.Windows.Controls.MenuItem { Header = "\u6536\u85CF\u5230\u5206\u7C7B" };
            AddExternalCollectCategoryItem(collectTo, targets, null, "\u672A\u5206\u7C7B");
            foreach (var category in _categories) AddExternalCollectCategoryItem(collectTo, targets, category.Id, category.Name);
            menu.Items.Add(collectTo);
            return;
        }

        var ids = targets.Select(x => x.Id).ToArray();
        if (!_showTrash)
        {
            var edit = new System.Windows.Controls.MenuItem
            {
                Header = targets.Length > 1 ? "\u6279\u91CF\u4FEE\u6539\u6807\u7B7E\u548C\u5907\u6CE8" : "\u4FEE\u6539\u6807\u7B7E\u548C\u5907\u6CE8",
                Tag = targets
            };
            edit.Click += EditMetadataClick;
            menu.Items.Add(edit);
            menu.Items.Add(new Separator());

            var move = new System.Windows.Controls.MenuItem { Header = "\u79FB\u52A8\u5230\u5206\u7C7B" };
            AddMoveCategoryItem(move, ids, null, "\u672A\u5206\u7C7B");
            foreach (var category in _categories) AddMoveCategoryItem(move, ids, category.Id, category.Name);
            menu.Items.Add(move);
            menu.Items.Add(new Separator());
        }

        var trash = new System.Windows.Controls.MenuItem { Header = _showTrash ? "\u4ECE\u56DE\u6536\u7AD9\u6062\u590D" : "\u79FB\u5230\u56DE\u6536\u7AD9", Tag = ids };
        trash.Click += DeleteCardClick;
        menu.Items.Add(trash);
    }

    private async void DeleteCardClick(object sender, RoutedEventArgs e)
    {
        var ids = (sender as FrameworkElement)?.Tag as long[];
        if (ids is null || ids.Length == 0)
        {
            var card = GetCardFromMenuSender(sender);
            ids = GetOperationTargetEntries(card).Where(x => !x.IsExternal).Select(x => x.Id).ToArray();
        }

        await ApplyTrashActionAsync(ids);
    }

    private async Task ApplyTrashActionAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        foreach (var id in ids)
        {
            if (_showTrash) await _repository.RestoreAsync(id);
            else await _repository.MoveToTrashAsync(id);
        }

        ToastService.Show(this, _showTrash ? $"\u5DF2\u6062\u590D {ids.Count} \u5F20\u56FE\u7247" : $"\u5DF2\u79FB\u5165\u56DE\u6536\u7AD9 {ids.Count} \u5F20\u56FE\u7247");
        FinishSelectionOperation();
        await RefreshAsync();
    }

    private async void EditMetadataClick(object sender, RoutedEventArgs e)
    {
        var targets = (sender as FrameworkElement)?.Tag as GalleryEntry[];
        if (targets is null || targets.Length == 0)
        {
            var card = GetCardFromMenuSender(sender);
            targets = GetOperationTargetEntries(card).Where(x => !x.IsExternal).ToArray();
        }

        targets = targets.Where(x => !x.IsExternal).ToArray();
        if (targets.Length == 0) return;

        var dialog = new EditMetadataDialog(
            targets.Length,
            CommonText(targets.Select(x => x.Tags)),
            CommonText(targets.Select(x => x.Notes)),
            targets.Length > 1)
        { Owner = this };
        if (dialog.ShowDialog() != true) return;

        await _repository.UpdateItemsMetadataAsync(
            targets.Select(x => x.Id),
            dialog.ApplyTags ? dialog.Tags : null,
            dialog.ApplyNotes ? dialog.Notes : null);

        ToastService.Show(this, targets.Length > 1 ? $"\u5DF2\u6279\u91CF\u4FEE\u6539 {targets.Length} \u5F20" : "\u6807\u7B7E\u548C\u5907\u6CE8\u5DF2\u4FDD\u5B58");
        FinishSelectionOperation();
        await RefreshAsync();
    }

    private static string CommonText(IEnumerable<string> values)
    {
        var list = values.ToArray();
        if (list.Length == 0) return string.Empty;
        var first = list[0];
        return list.All(x => string.Equals(x, first, StringComparison.Ordinal)) ? first : string.Empty;
    }

    private void AddMoveCategoryItem(System.Windows.Controls.MenuItem parent, long[] ids, long? categoryId, string name)
    {
        var item = new System.Windows.Controls.MenuItem { Header = name, Tag = new CategoryMove(ids, categoryId, name) };
        item.Click += MoveToCategoryClick;
        parent.Items.Add(item);
    }

    private async void MoveToCategoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryMove move) return;
        await _repository.UpdateItemsCategoryAsync(move.ItemIds, move.CategoryId);
        ToastService.Show(this, $"\u5DF2\u79FB\u52A8 {move.ItemIds.Length} \u5F20\u5230 {move.Name}");
        FinishSelectionOperation();
        await RefreshAsync();
    }

    private void AddExternalCollectCategoryItem(System.Windows.Controls.MenuItem parent, GalleryEntry[] entries, long? categoryId, string name)
    {
        var item = new System.Windows.Controls.MenuItem { Header = name, Tag = new ExternalCollect(entries, categoryId) };
        item.Click += CollectExternalClick;
        parent.Items.Add(item);
    }

    private async void CollectExternalClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExternalCollect collect) return;
        await CollectExternalEntriesAsync(collect.Entries, collect.CategoryId);
    }

    private async Task CollectExternalEntriesAsync(IReadOnlyList<GalleryEntry> entries, long? categoryId)
    {
        var external = entries.Where(x => x.IsExternal && File.Exists(x.OriginalPath)).ToArray();
        if (external.Length == 0)
        {
            ShowSubtleStatus("\u6CA1\u6709\u53EF\u6536\u85CF\u7684\u5916\u90E8\u56FE\u7247");
            return;
        }

        var saved = 0;
        var duplicates = 0;
        foreach (var entry in external)
        {
            try
            {
                var pending = await _capture.CreateFromFileAsync(entry.OriginalPath);
                var result = await _capture.SaveAsync(pending, entry.Prompt, entry.Notes, categoryId, []);
                if (result.WasDuplicate) duplicates++; else saved++;
            }
            catch { }
        }

        FinishSelectionOperation();
        var message = duplicates > 0 && saved == 0
            ? $"\u5DF2\u5728\u56FE\u5E93\u4E2D - {duplicates} \u5F20"
            : duplicates > 0
                ? $"\u5DF2\u6536\u85CF {saved} \u5F20\uFF0C\u5DF2\u5B58\u5728 {duplicates} \u5F20"
                : $"\u5DF2\u6536\u85CF {saved} \u5F20";
        ShowSubtleStatus(message);
    }

    private async Task CollectCurrentExternalSelectionAsync()
    {
        if (!IsExternalMode) return;
        GalleryEntry[] targets;
        if (_viewerIndex >= 0 && _viewerIndex < _items.Count && _items[_viewerIndex].IsExternal)
            targets = [_items[_viewerIndex]];
        else
            targets = _selectedItemIds.Count > 0 ? _items.Where(x => _selectedItemIds.Contains(x.Id)).ToArray() : [];
        await CollectExternalEntriesAsync(targets, null);
    }

    private IEnumerable<GalleryEntry> GetOperationTargetEntries(GalleryCardViewModel? card)
    {
        if (card is not null && _selectedItemIds.Contains(card.Id)) return _items.Where(x => _selectedItemIds.Contains(x.Id)).ToArray();
        if (card is null && _selectedItemIds.Count > 0) return _items.Where(x => _selectedItemIds.Contains(x.Id)).ToArray();
        return card is null ? [] : [card.Item];
    }

    private string ResolveOriginalPath(GalleryEntry entry) => entry.IsExternal ? entry.OriginalPath : _repository.Paths.ToAbsolute(entry.OriginalPath);

    private static GalleryCardViewModel? GetCardFromMenuSender(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is GalleryCardViewModel direct) return direct;
        if (sender is System.Windows.Controls.MenuItem { Parent: System.Windows.Controls.ContextMenu { DataContext: GalleryCardViewModel fromMenu } }) return fromMenu;
        return null;
    }


    private void SidebarListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item) return;
        item.IsSelected = true;
        e.Handled = false;
    }

    private void CategoryListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (CategoryList.ContextMenu is not { } menu || CategoryList.SelectedItem is not CategoryChoice { Id: > 0 } choice)
        {
            e.Handled = true;
            return;
        }

        HoldLeftPanelForMenu(menu);
        menu.Items.Clear();
        menu.Background = new SolidColorBrush(Color.FromArgb(245, 17, 28, 42));
        menu.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        var categoryIndex = _categories.ToList().FindIndex(x => x.Id == choice.Id.Value);
        AddSidebarMenuItem(menu, "\u4E0A\u79FB", categoryIndex > 0, async () => await MoveCategoryAsync(choice.Id.Value, -1));
        AddSidebarMenuItem(menu, "\u4E0B\u79FB", categoryIndex >= 0 && categoryIndex < _categories.Count - 1, async () => await MoveCategoryAsync(choice.Id.Value, 1));
        menu.Items.Add(new Separator());
        AddSidebarMenuItem(menu, "\u5220\u9664\u5206\u7C7B", true, async () => await DeleteCategoryFromMenuAsync(choice.Id.Value, choice.Name));
    }

    private async Task MoveCategoryAsync(long categoryId, int direction)
    {
        await _repository.MoveCategoryAsync(categoryId, direction);
        await LoadCategoriesAsync();
        if (CategoryList.ItemsSource is IEnumerable<CategoryChoice> choices)
            CategoryList.SelectedItem = choices.FirstOrDefault(x => x.Id == categoryId);
        await RefreshAsync();
    }

    private async Task DeleteCategoryFromMenuAsync(long categoryId, string name)
    {
        var confirm = MessageBox.Show(this, $"\u5220\u9664\u5206\u7C7B\u201C{name}\u201D\uFF1F\u8FD9\u4E2A\u5206\u7C7B\u91CC\u7684\u56FE\u7247\u4F1A\u4FDD\u7559\uFF0C\u5E76\u79FB\u52A8\u5230\u672A\u5206\u7C7B\u3002", "\u5220\u9664\u5206\u7C7B", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        await _repository.DeleteCategoryAsync(categoryId);
        await LoadCategoriesAsync();
        await RefreshAsync();
        ToastService.Show(this, "\u5206\u7C7B\u5DF2\u5220\u9664\uFF0C\u56FE\u7247\u5DF2\u79FB\u5230\u672A\u5206\u7C7B");
    }

    private void ExternalFolderListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ExternalFolderList.ContextMenu is not { } menu || ExternalFolderList.SelectedItem is not ExternalFolderChoice choice)
        {
            e.Handled = true;
            return;
        }

        HoldLeftPanelForMenu(menu);
        menu.Items.Clear();
        menu.Background = new SolidColorBrush(Color.FromArgb(245, 17, 28, 42));
        menu.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        var index = _settings.ExternalFolders.FindIndex(x => x.Id == choice.Id);
        AddSidebarMenuItem(menu, "\u4E0A\u79FB", index > 0, async () => await MoveExternalFolderAsync(choice.Id, -1));
        AddSidebarMenuItem(menu, "\u4E0B\u79FB", index >= 0 && index < _settings.ExternalFolders.Count - 1, async () => await MoveExternalFolderAsync(choice.Id, 1));
        menu.Items.Add(new Separator());
        AddSidebarMenuItem(menu, "\u79FB\u9664\u6587\u4EF6\u5939", true, async () => await RemoveExternalFolderAsync(choice.Id));
    }

    private async Task MoveExternalFolderAsync(string id, int direction)
    {
        var index = _settings.ExternalFolders.FindIndex(x => x.Id == id);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= _settings.ExternalFolders.Count) return;
        (_settings.ExternalFolders[index], _settings.ExternalFolders[target]) = (_settings.ExternalFolders[target], _settings.ExternalFolders[index]);
        _settings.Save();
        LoadExternalFolders();
        if (ExternalFolderList.ItemsSource is IEnumerable<ExternalFolderChoice> choices)
            ExternalFolderList.SelectedItem = choices.FirstOrDefault(x => x.Id == id);
        await RefreshAsync();
    }

    private async Task RemoveExternalFolderAsync(string id)
    {
        var removed = _settings.ExternalFolders.RemoveAll(x => x.Id == id) > 0;
        if (!removed) return;
        _settings.Save();
        var wasSelected = _externalFolderId == id;
        if (wasSelected) _externalFolderId = null;
        LoadExternalFolders();
        if (wasSelected)
        {
            _suppressFilterRefresh = true;
            CategoryList.SelectedIndex = 0;
            _suppressFilterRefresh = false;
        }
        await RefreshAsync();
        ToastService.Show(this, "\u5916\u90E8\u6587\u4EF6\u5939\u5DF2\u4ECE\u4FA7\u680F\u79FB\u9664\uFF0C\u78C1\u76D8\u6587\u4EF6\u672A\u5220\u9664");
    }

    private static void AddSidebarMenuItem(System.Windows.Controls.ContextMenu menu, string header, bool isEnabled, Func<Task> action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header, IsEnabled = isEnabled };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }


    private static T? FindDescendant<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
    private sealed record CategoryMove(long[] ItemIds, long? CategoryId, string Name);
    private sealed record ExternalCollect(GalleryEntry[] Entries, long? CategoryId);
    private sealed record CategoryChoice(long? Id, string Name);
    private sealed record ExternalFolderChoice(string Id, string Name, string Path);
}
