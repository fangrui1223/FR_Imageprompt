using System.Windows;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class CaptureCapsuleWindow : Window
{
    private Guid? _captureId;
    private CapsuleAction _primaryAction;

    public CaptureCapsuleWindow()
    {
        InitializeComponent();
        ShowInTaskbar = DevelopmentPerformanceTrace.IsEnabled;
        Loaded += (_, _) => PositionAtBottomRight();
    }

    public event Action<Guid>? ExpandRequested;
    public event Action<Guid>? UndoRequested;
    public event Action? InboxRequested;

    public void ShowDetected()
    {
        _captureId = null;
        _primaryAction = CapsuleAction.None;
        PreviewImage.Source = null;
        PreparingGlyph.Visibility = Visibility.Visible;
        StateText.Text = "发现图片，正在准备…";
        DetailText.Text = "不会打断当前操作";
        ErrorText.Visibility = Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Collapsed;
        ShowWithoutActivation();
    }

    public void UpdateSession(
        CaptureSessionRecord session,
        BitmapSource? preview,
        int inboxCount)
    {
        _captureId = session.Id;
        PreviewImage.Source = preview;
        PreparingGlyph.Visibility = preview is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorText.Text = session.Error ?? "";
        ErrorText.Visibility = string.IsNullOrWhiteSpace(session.Error)
            ? Visibility.Collapsed
            : Visibility.Visible;
        InboxButton.Content = $"待补 {inboxCount}";
        InboxButton.Visibility = inboxCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        (StateText.Text, DetailText.Text, _primaryAction, PrimaryButton.Content) =
            Describe(session);
        PrimaryButton.Visibility = _primaryAction == CapsuleAction.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        ShowWithoutActivation();
    }

    public void UpdateInboxCount(int inboxCount)
    {
        InboxButton.Content = $"待补 {inboxCount}";
        InboxButton.Visibility = inboxCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void HideCapsule()
    {
        if (IsVisible) Hide();
    }

    private void ShowWithoutActivation()
    {
        if (!IsVisible) Show();
        PositionAtBottomRight();
    }

    private static (string State, string Detail, CapsuleAction Action, string Button) Describe(
        CaptureSessionRecord session) =>
        session.State switch
        {
            CaptureState.ImageDetected or CaptureState.PreparingImage =>
                ("正在准备图片…", "图片处理完成后会自动等待提示词", CapsuleAction.None, ""),
            CaptureState.WaitingForPrompt =>
                ("等待提示词", "复制下一段完整文本即可自动收录", CapsuleAction.Expand, "展开"),
            CaptureState.PromptDebouncing =>
                ("正在确认提示词…", "文本稳定后自动保存", CapsuleAction.Expand, "编辑"),
            CaptureState.Saved when !string.IsNullOrWhiteSpace(session.AiSummary) =>
                ("AI 已完成", session.AiSummary, HasUndoTime(session) ? CapsuleAction.Undo : CapsuleAction.None, "撤销"),
            CaptureState.Saved when session.WasDuplicate =>
                ("已更新已有图片", "重复图片已静默合并 · 10 秒内可撤销", HasUndoTime(session) ? CapsuleAction.Undo : CapsuleAction.None, "撤销"),
            CaptureState.Saved =>
                ("已自动收录", "已保存到图库 · 10 秒内可撤销", HasUndoTime(session) ? CapsuleAction.Undo : CapsuleAction.None, "撤销"),
            CaptureState.WaitingForAi =>
                ("已保存，AI 后台处理中", "AI 不可用也不会影响本次收录", HasUndoTime(session) ? CapsuleAction.Undo : CapsuleAction.None, "撤销"),
            CaptureState.NeedsPrompt =>
                ("已进入待补提示词", "图片已安全保留，可稍后补充", CapsuleAction.Expand, "补充"),
            CaptureState.Failed =>
                ("收录需要处理", "图片和会话已保留", CapsuleAction.Expand, "查看"),
            CaptureState.Undone =>
                ("已撤销收录", "本次操作已经还原", CapsuleAction.None, ""),
            _ => ("收录状态已更新", "", CapsuleAction.None, "")
        };

    private static bool HasUndoTime(CaptureSessionRecord session) =>
        session.UndoDeadlineAt is { } deadline && deadline > DateTimeOffset.UtcNow;

    private void PrimaryButtonClick(object sender, RoutedEventArgs e)
    {
        if (_captureId is not { } captureId) return;
        if (_primaryAction == CapsuleAction.Undo)
        {
            UndoRequested?.Invoke(captureId);
        }
        else if (_primaryAction == CapsuleAction.Expand)
        {
            ExpandRequested?.Invoke(captureId);
        }
    }

    private void InboxButtonClick(object sender, RoutedEventArgs e) =>
        InboxRequested?.Invoke();

    private void PositionAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Bottom - ActualHeight - 24;
    }

    private enum CapsuleAction
    {
        None,
        Expand,
        Undo
    }
}
