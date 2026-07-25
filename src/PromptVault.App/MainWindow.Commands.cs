using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private readonly List<CommandPaletteItem> _commandCatalog = [];
    private bool _commandExecuting;

    public ObservableCollection<CommandPaletteItem> CommandResults { get; } = [];

    private void FocusInstantSearch()
    {
        if (ImmersiveViewer.Visibility == Visibility.Visible) CloseImmersiveViewer();
        CloseCommandPalette();
        UpdateTopPanelHeight();
        ShowTopPanel();
        _topHideTimer.Stop();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }));
        DevelopmentPerformanceTrace.Event("instant-search-focus", new
        {
            queryLength = SearchBox.Text.Length,
            rowsOpacity = RowsList.Opacity
        });
    }

    private void SearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FocusGalleryFromSearch();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Down or Key.Enter)) return;

        FocusGalleryFromSearch();
        var itemId = _selectionFocusId is { } selectedId
            && _items.Any(item => item.Id == selectedId)
                ? selectedId
                : _items.FirstOrDefault()?.Id;
        if (itemId is not null)
        {
            SelectSingle(itemId.Value);
            ScrollSelectionIntoView(itemId.Value);
        }
        e.Handled = true;
    }

    private void FocusGalleryFromSearch()
    {
        FocusGalleryInput();
        RowsList.Focus();
        Keyboard.Focus(RowsList);
        if (!_settings.EdgeMenusAlwaysVisible) HideTopPanel();
    }

    private void FocusTagFilter()
    {
        CloseCommandPalette();
        UpdateTopPanelHeight();
        ShowTopPanel();
        _topHideTimer.Stop();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            TagBox.Focus();
            Keyboard.Focus(TagBox);
            TagBox.SelectAll();
        }));
    }

    private void ToggleCommandPalette()
    {
        if (CommandPaletteOverlay.Visibility == Visibility.Visible)
        {
            CloseCommandPalette();
            return;
        }

        if (ImmersiveViewer.Visibility == Visibility.Visible) CloseImmersiveViewer();
        var stopwatch = Stopwatch.StartNew();
        RebuildCommandCatalog();
        CommandSearchBox.Text = "";
        ApplyCommandFilter();
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        CommandPaletteOverlay.IsHitTestVisible = true;
        CommandList.SelectedIndex = CommandResults.Count > 0 ? 0 : -1;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            CommandSearchBox.Focus();
            Keyboard.Focus(CommandSearchBox);
        }));
        stopwatch.Stop();
        DevelopmentPerformanceTrace.Event("command-palette-open", new
        {
            commands = _commandCatalog.Count,
            visibleCommands = CommandResults.Count,
            elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            rowsOpacity = RowsList.Opacity
        });
    }

    private void CloseCommandPalette()
    {
        if (CommandPaletteOverlay is null) return;
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        CommandPaletteOverlay.IsHitTestVisible = false;
    }

    private void RebuildCommandCatalog()
    {
        _commandCatalog.Clear();
        var layout = CurrentGalleryLayoutPreference();
        _commandCatalog.AddRange(
        [
            new("search.focus", "搜索图片", "聚焦即时搜索，输入后保持旧画面直到新结果就绪", "find ctrl f everything prompt", "搜索"),
            new("filter.all", "筛选：全部图片", "清除搜索、标签、分类、收藏和来源筛选", "clear reset all", "筛选"),
            new(
                "filter.favorite",
                _favoritesOnly ? "筛选：取消只看收藏" : "筛选：只看收藏",
                _favoritesOnly ? "恢复显示当前条件下的全部图片" : "只显示已收藏图片",
                "favorite star collection",
                "筛选"),
            new("filter.tag.input", "筛选：输入标签", "聚焦标签筛选框，可输入任意一个或多个标签", "tag label", "筛选"),
            new("source.library", "来源：主图库", "切换回受管图库", "library managed source", "来源"),
            new("layout.waterfall", "布局：瀑布流", layout.Mode == GalleryLayoutPreference.WaterfallMode ? "当前布局" : "切换到虚拟化瀑布流", "waterfall masonry", "布局"),
            new("layout.justified", "布局：等高拼接", layout.Mode == GalleryLayoutPreference.JustifiedMode ? "当前布局" : "切换到等高拼接", "justified equal height", "布局"),
            new("density.compact", "密度：紧凑", "间距 8，目标尺寸 240", "compact small", "布局"),
            new("density.comfortable", "密度：舒适", "间距 14，目标尺寸 320", "comfortable medium", "布局"),
            new("density.spacious", "密度：宽松", "间距 22，目标尺寸 400", "spacious large", "布局"),
            new("board.current", "画板：加入当前图片", "M5 持久画板入口", "board canvas current", "画板"),
            new("settings.edge.sensitivity", "设置：切换边缘灵敏度", $"当前为 {EdgeSensitivityLabel()}", "edge sensitivity", "设置"),
            new("settings.edge.always", _settings.EdgeMenusAlwaysVisible ? "设置：关闭边栏常显" : "设置：边栏始终显示", "顶部和左侧菜单显示偏好", "edge menu always visible", "设置"),
            new("settings.motion", _settings.ReducedMotionEnabled ? "设置：恢复界面动效" : "设置：减少界面动效", "切换所有装饰性动效", "motion animation accessibility", "设置"),
            new("settings.transparent", "设置：切换透明模式", "复用当前窗口状态切换显示模式", "transparent glass ctrl m", "设置"),
            new("future.pending", "待校正图片", "入口已预留；M4 产生 AI 草稿后启用状态筛选", "pending review correction ai", "未来入口"),
            new("future.aesthetic", "AI 审美属性", "入口已预留；M4 分析后提供属性筛选", "aesthetic composition color lighting ai", "未来入口"),
            new("future.similar", "查找相似图片", "入口已预留；M4 向量索引完成后启用", "similar image vector", "未来入口")
        ]);

        foreach (var category in _categories)
        {
            _commandCatalog.Add(new CommandPaletteItem(
                "filter.category",
                $"分类：{category.Name}",
                "打开主图库分类",
                $"category {category.Name}",
                "分类",
                category.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var tags = _items
            .Where(item => !item.IsExternal)
            .SelectMany(item => SplitPaletteTags(item.Tags))
            .GroupBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(18)
            .Select(group => group.Key);
        foreach (var tag in tags)
        {
            _commandCatalog.Add(new CommandPaletteItem(
                "filter.tag",
                $"标签：{tag}",
                "在主图库中按标签筛选",
                $"tag label {tag}",
                "标签",
                tag));
        }

        foreach (var folder in _settings.ExternalFolders)
        {
            _commandCatalog.Add(new CommandPaletteItem(
                "source.external",
                $"来源：{folder.Name}",
                "打开外部文件夹索引",
                $"external folder source {folder.Name}",
                "来源",
                folder.Id));
        }
    }

    private static IEnumerable<string> SplitPaletteTags(string tags) =>
        tags.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length > 0);

    private void CommandSearchChanged(object sender, TextChangedEventArgs e) => ApplyCommandFilter();

    private void ApplyCommandFilter()
    {
        var selectedId = (CommandList.SelectedItem as CommandPaletteItem)?.Id;
        var selectedArgument = (CommandList.SelectedItem as CommandPaletteItem)?.Argument;
        var filtered = CommandPaletteSearch.Filter(_commandCatalog, CommandSearchBox.Text);
        CommandResults.Clear();
        foreach (var command in filtered) CommandResults.Add(command);
        CommandList.SelectedItem = CommandResults.FirstOrDefault(command =>
            command.Id == selectedId && command.Argument == selectedArgument);
        if (CommandList.SelectedItem is null && CommandResults.Count > 0) CommandList.SelectedIndex = 0;
        CommandEmptyState.Visibility = CommandResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandResultCount.Text = $"{CommandResults.Count} 个命令";
    }

    private void CommandSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseCommandPalette();
            FocusGalleryInput();
            Focus();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            if (CommandResults.Count == 0) return;
            var offset = e.Key == Key.Down ? 1 : -1;
            CommandList.SelectedIndex = Math.Clamp(
                CommandList.SelectedIndex + offset,
                0,
                CommandResults.Count - 1);
            CommandList.ScrollIntoView(CommandList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            ExecuteSelectedCommand();
            e.Handled = true;
        }
    }

    private void CommandListDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelectedCommand();

    private void CommandPaletteOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, CommandPaletteOverlay)) return;
        CloseCommandPalette();
        Focus();
    }

    private async void ExecuteSelectedCommand()
    {
        if (_commandExecuting || CommandList.SelectedItem is not CommandPaletteItem command) return;
        _commandExecuting = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            CloseCommandPalette();
            switch (command.Id)
            {
                case "search.focus":
                    FocusInstantSearch();
                    break;
                case "filter.all":
                    await ApplyLibraryCommandFilterAsync(
                        setCategory: true,
                        categoryId: null,
                        tag: "",
                        favoritesOnly: false,
                        clearQuery: true);
                    break;
                case "filter.favorite":
                    await ApplyLibraryCommandFilterAsync(
                        setCategory: false,
                        categoryId: null,
                        tag: null,
                        favoritesOnly: !_favoritesOnly,
                        clearQuery: false);
                    break;
                case "filter.tag.input":
                    FocusTagFilter();
                    break;
                case "filter.category":
                    await ApplyLibraryCommandFilterAsync(
                        setCategory: true,
                        categoryId: long.Parse(
                            command.Argument!,
                            System.Globalization.CultureInfo.InvariantCulture),
                        tag: null,
                        favoritesOnly: _favoritesOnly,
                        clearQuery: false);
                    break;
                case "filter.tag":
                    await ApplyLibraryCommandFilterAsync(
                        setCategory: false,
                        categoryId: null,
                        tag: command.Argument,
                        favoritesOnly: _favoritesOnly,
                        clearQuery: false);
                    break;
                case "source.library":
                    await ApplyLibraryCommandFilterAsync(
                        setCategory: false,
                        categoryId: null,
                        tag: null,
                        favoritesOnly: _favoritesOnly,
                        clearQuery: false);
                    break;
                case "source.external":
                    await ApplyExternalSourceCommandAsync(command.Argument!);
                    break;
                case "layout.waterfall":
                    SetLayoutModeFromCommand(GalleryLayoutPreference.WaterfallMode);
                    break;
                case "layout.justified":
                    SetLayoutModeFromCommand(GalleryLayoutPreference.JustifiedMode);
                    break;
                case "density.compact":
                    SetLayoutDensityFromCommand(GalleryLayoutPreference.CompactDensity);
                    break;
                case "density.comfortable":
                    SetLayoutDensityFromCommand(GalleryLayoutPreference.ComfortableDensity);
                    break;
                case "density.spacious":
                    SetLayoutDensityFromCommand(GalleryLayoutPreference.SpaciousDensity);
                    break;
                case "board.current":
                    ToastService.Show(this, "画板入口已预留，M5 将启用持久画板");
                    break;
                case "settings.edge.sensitivity":
                    EdgeSensitivityClick(this, new RoutedEventArgs());
                    ToastService.Show(this, $"边缘灵敏度：{EdgeSensitivityLabel()}");
                    break;
                case "settings.edge.always":
                    EdgeAlwaysVisibleClick(this, new RoutedEventArgs());
                    break;
                case "settings.motion":
                    _settings.ReducedMotionEnabled = !_settings.ReducedMotionEnabled;
                    _settings.Save();
                    ApplyTransparentMode();
                    ToastService.Show(this, _settings.ReducedMotionEnabled ? "已减少界面动效" : "已恢复界面动效");
                    break;
                case "settings.transparent":
                    ToggleTransparentMode();
                    break;
                case "future.pending":
                    ToastService.Show(this, "待校正状态将在 M4 AI 草稿生成后启用");
                    break;
                case "future.aesthetic":
                    ToastService.Show(this, "AI 审美属性将在 M4 本地分析后启用");
                    break;
                case "future.similar":
                    ToastService.Show(this, "相似图片入口已预留，M4 向量索引完成后启用");
                    break;
            }
        }
        finally
        {
            stopwatch.Stop();
            _commandExecuting = false;
            DevelopmentPerformanceTrace.Event("command-palette-execute", new
            {
                command = command.Id,
                elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                rowsOpacity = RowsList.Opacity
            });
        }
    }

    private async Task ApplyLibraryCommandFilterAsync(
        bool setCategory,
        long? categoryId,
        string? tag,
        bool favoritesOnly,
        bool clearQuery)
    {
        _searchTimer.Stop();
        _favoritesOnly = favoritesOnly;
        _showTrash = false;
        _externalFolderId = null;
        _suppressExternalRefresh = true;
        ExternalFolderList.SelectedIndex = -1;
        _suppressExternalRefresh = false;
        if (setCategory && CategoryList.ItemsSource is IEnumerable<CategoryChoice> choices)
        {
            _suppressFilterRefresh = true;
            CategoryList.SelectedItem = choices.FirstOrDefault(choice => choice.Id == categoryId);
            _suppressFilterRefresh = false;
        }
        if (tag is not null) TagBox.Text = tag;
        if (clearQuery) SearchBox.Text = "";
        _searchTimer.Stop();
        FinishSelectionOperation();
        UpdateTrashVisual();
        UpdateLayoutControlVisuals();
        await RefreshAsync(RefreshAnimationKind.ViewSwitch);
    }

    private async Task ApplyExternalSourceCommandAsync(string folderId)
    {
        if (ExternalFolderList.ItemsSource is not IEnumerable<ExternalFolderChoice> choices) return;
        var choice = choices.FirstOrDefault(candidate => candidate.Id == folderId);
        if (choice is null) return;
        _searchTimer.Stop();
        _favoritesOnly = false;
        _showTrash = false;
        _externalFolderId = folderId;
        _suppressExternalRefresh = true;
        ExternalFolderList.SelectedItem = choice;
        _suppressExternalRefresh = false;
        _suppressFilterRefresh = true;
        CategoryList.SelectedIndex = -1;
        _suppressFilterRefresh = false;
        FinishSelectionOperation();
        UpdateTrashVisual();
        UpdateLayoutControlVisuals();
        await RefreshAsync(RefreshAnimationKind.ViewSwitch);
    }

    private void SetLayoutModeFromCommand(string mode)
    {
        if (string.Equals(CurrentGalleryLayoutPreference().Mode, mode, StringComparison.Ordinal)) return;
        ChangeGalleryLayout(preference => preference.Mode = mode);
    }

    private void SetLayoutDensityFromCommand(string density)
    {
        if (string.Equals(CurrentGalleryLayoutPreference().Density, density, StringComparison.Ordinal)) return;
        ChangeGalleryLayout(preference => preference.ApplyDensity(density));
    }

    private string EdgeSensitivityLabel() => EdgeIntentProfile.NormalizeSensitivity(
        _settings.EdgeMenuSensitivity) switch
    {
        EdgeIntentProfile.LowSensitivity => "低",
        EdgeIntentProfile.HighSensitivity => "高",
        _ => "标准"
    };
}
