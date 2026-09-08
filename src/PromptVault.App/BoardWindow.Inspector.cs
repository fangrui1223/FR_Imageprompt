using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private readonly DispatcherTimer _inspectorCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };

    private void ToggleInspector()
    {
        if (BoardInspector.Visibility == Visibility.Visible)
        {
            CloseInspector();
            return;
        }
        UpdateInspectorContent();
        BoardInspector.Visibility = Visibility.Visible;
        BoardInspector.IsHitTestVisible = true;
        SetStatus("属性已显示");
    }

    private void CloseInspector()
    {
        _inspectorCloseTimer.Stop();
        BoardInspector.Visibility = Visibility.Collapsed;
        BoardInspector.IsHitTestVisible = false;
        if (BoardInspector.IsKeyboardFocusWithin) Keyboard.Focus(BoardViewport);
    }

    private async void InspectorButtonClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.ShowInspector);

    private void CloseInspectorClick(object sender, RoutedEventArgs e) => CloseInspector();

    private void BoardInspectorMouseEnter(object sender, MouseEventArgs e) => _inspectorCloseTimer.Stop();

    private void BoardInspectorMouseLeave(object sender, MouseEventArgs e)
    {
        _inspectorCloseTimer.Stop();
        _inspectorCloseTimer.Tick -= InspectorCloseTimerTick;
        _inspectorCloseTimer.Tick += InspectorCloseTimerTick;
        _inspectorCloseTimer.Start();
    }

    private void InspectorCloseTimerTick(object? sender, EventArgs e)
    {
        if (BoardInspector.IsMouseOver
            || BoardInspector.IsKeyboardFocusWithin
            || OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return;
        CloseInspector();
    }

    private void UpdateInspectorContent()
    {
        var currentBoard = _boards.FirstOrDefault(board => board.Id == CurrentBoardId);
        if (_selectedIds.Count > 0 && _selectedNoteIds.Count > 0)
        {
            SelectionSummaryText.Text = $"已选择 {_selectedIds.Count} 张图片、{_selectedNoteIds.Count} 个便签";
            InspectorDetailsText.Text = "可共同移动或移除；图片排版只移动所选图片。";
            SourceStateText.Text = "单独选择图片或便签后，可使用对应的尺寸与样式编辑。";
            UpdateNotePropertiesPanel();
            return;
        }
        if (SingleSelectedNoteId is { } noteId)
        {
            var note = _notes.SingleOrDefault(candidate => candidate.Id == noteId);
            SelectionSummaryText.Text = note is null ? "未选择内容" : "便签属性";
            InspectorDetailsText.Text = note is null
                ? "便签已不存在"
                : $"尺寸 {note.Width:N0} × {note.Height:N0} DIP\n层级 {note.ZIndex}";
            SourceStateText.Text = "便签只保存在当前画板；双击便签可直接编辑文字。";
            UpdateNotePropertiesPanel();
            return;
        }

        if (_selectedNoteIds.Count > 1)
        {
            SelectionSummaryText.Text = $"已选择 {_selectedNoteIds.Count} 个便签";
            InspectorDetailsText.Text = "可共同移动、缩放、删除、调整层级，并从右键菜单批量应用预设。";
            SourceStateText.Text = "属性面板只编辑单个便签；取消多选后可继续编辑具体样式。";
            UpdateNotePropertiesPanel();
            return;
        }

        var selected = _items.Where(item => _selectedIds.Contains(item.Id)).ToArray();
        if (selected.Length == 1)
        {
            var item = selected[0];
            var path = ResolveItemOriginalPath(item);
            var ratio = SimplifiedRatio(item.NaturalWidth, item.NaturalHeight);
            var group = item.GroupId is { } groupId
                ? _groups.FirstOrDefault(candidate => candidate.Id == groupId)?.Name ?? "未命名分组"
                : "未分组";
            SelectionSummaryText.Text = "图片属性";
            InspectorDetailsText.Text =
                $"原始尺寸 {item.NaturalWidth:N0} × {item.NaturalHeight:N0} px\n"
                + $"宽高比 {ratio}\n画板尺寸 {item.Width:N1} × {item.Height:N1} DIP\n"
                + $"旋转 {item.Rotation:N1}°\n裁剪 L {item.CropLeft:P0} · T {item.CropTop:P0} · R {item.CropRight:P0} · B {item.CropBottom:P0}\n"
                + $"分组 {group}";
            SourceStateText.Text = File.Exists(path)
                ? $"原图可用\n{path}"
                : $"原图缺失，可主动重新定位\n记录路径：{item.SourcePathOverride ?? item.SourcePathSnapshot}";
            return;
        }

        if (selected.Length > 1)
        {
            var groupIds = selected.Select(item => item.GroupId).Distinct().ToArray();
            var groupLabel = groupIds.Length == 1
                ? groupIds[0] is { } groupId
                    ? _groups.FirstOrDefault(candidate => candidate.Id == groupId)?.Name ?? "未命名分组"
                    : "均未分组"
                : "混合";
            var rotations = selected.Select(item => item.Rotation).Distinct().Count() == 1
                ? $"{selected[0].Rotation:N1}°"
                : "混合";
            SelectionSummaryText.Text = $"已选择 {selected.Length} 张图片";
            InspectorDetailsText.Text = $"共同分组 {groupLabel}\n旋转 {rotations}\n可批量执行层级、分组、裁剪和变换命令";
            SourceStateText.Text = "多选不会修改图库原图；“从画板移除”只删除画板引用。";
            return;
        }

        SelectionSummaryText.Text = "画板属性";
        InspectorDetailsText.Text =
            $"{currentBoard?.Name ?? "当前画板"}\n图片 {_items.Count:N0} · 便签 {_notes.Count:N0}\n背景 {BackgroundName(currentBoard?.BackgroundStyle)}";
        SourceStateText.Text = "未选择对象。可在这里切换背景或新建便签。";
        UpdateNotePropertiesPanel();
    }

    private static string SimplifiedRatio(int width, int height)
    {
        if (width <= 0 || height <= 0) return "未知";
        var divisor = GreatestCommonDivisor(width, height);
        return $"{width / divisor}:{height / divisor}";
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Max(1, Math.Abs(left));
    }

    private static string BackgroundName(string? style) => style switch
    {
        "warm" => "暖调暗色",
        "paper" => "灰白纸面",
        "blueprint" => "蓝图网格",
        _ => "中性深色"
    };
}
