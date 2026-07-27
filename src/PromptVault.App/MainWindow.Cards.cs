using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private static readonly TimeSpan CardHoverIntentDelay = TimeSpan.FromMilliseconds(200);
    private readonly ConditionalWeakTable<Border, CancellationTokenSource> _cardHoverDelays = new();

    private async void CardMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border card) return;
        AnimateScale(card, 1.012);
        CancelCardHoverDelay(card);
        var cancellation = new CancellationTokenSource();
        _cardHoverDelays.Add(card, cancellation);
        try
        {
            await Task.Delay(CardHoverIntentDelay, cancellation.Token);
            if (!card.IsMouseOver || cancellation.IsCancellationRequested) return;
            SetCardOverlay(card, visible: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border card) return;
        CancelCardHoverDelay(card);
        SetCardOverlay(card, visible: false);
        AnimateScale(card, 1);
    }

    private void CardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card) return;
        CancelCardHoverDelay(card);
        SetCardOverlay(card, visible: false, animate: false);
    }

    private void CancelCardHoverDelay(Border card)
    {
        if (!_cardHoverDelays.TryGetValue(card, out var cancellation)) return;
        _cardHoverDelays.Remove(card);
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static void SetCardOverlay(Border card, bool visible, bool animate = true)
    {
        if (FindNamedDescendant<Border>(card, "HoverOverlay") is not { } overlay) return;
        overlay.IsHitTestVisible = visible;
        var target = visible ? 1d : 0d;
        var duration = animate
            ? VisualModeService.Motion(MotionToken.Fast)
            : TimeSpan.Zero;
        overlay.BeginAnimation(OpacityProperty, null);
        if (duration == TimeSpan.Zero)
        {
            overlay.Opacity = target;
            return;
        }
        overlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(target, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }
            if (FindNamedDescendant<T>(child, name) is { } descendant) return descendant;
        }
        return null;
    }

    private void CopyCardPromptClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        if (string.IsNullOrWhiteSpace(card.Item.Prompt))
        {
            ToastService.Show(this, "这张图片还没有提示词");
            return;
        }
        Clipboard.SetText(card.Item.Prompt);
        ToastService.Show(this, "提示词已复制");
    }

    private async void ToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        if (card.IsExternal)
        {
            ToastService.Show(this, "外部文件夹图片需要先收录后才能收藏");
            return;
        }

        var next = !card.IsFavorite;
        try
        {
            await _repository.SetFavoriteAsync(card.Id, next);
            card.IsFavorite = next;
            ToastService.Show(this, next ? "已收藏" : "已取消收藏");
            if (_favoritesOnly && !next)
            {
                await RefreshAsync(RefreshAnimationKind.ContentChange);
            }
        }
        catch (Exception ex)
        {
            ToastService.Show(this, $"收藏状态未保存：{ex.Message}");
        }
    }

    private async void AddCardToBoardClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        await AddEntriesToBoardAsync(GetOperationTargetEntries(card).ToArray());
    }

    private async Task AddEntriesToBoardAsync(IReadOnlyList<GalleryEntry> entries)
    {
        var selection = BoardTransferPolicy.Select(entries);
        var externalCount = selection.ExternalItemCount;
        var managed = selection.ManagedItems;
        if (managed.Count == 0)
        {
            ToastService.Show(this, externalCount > 0
                ? "外部文件夹图片需要先收录，再加入画板"
                : "请先选择一张图库图片");
            return;
        }
        try
        {
            await _boardWorkspace.AddAsync(managed, this);
            ToastService.Show(this, $"已加入画板：{managed.Count} 张");
            if (externalCount > 0) ToastService.Show(this, $"另有 {externalCount} 张外部图片需先收录");
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-add", "Gallery items could not be added to a board.", ex);
            ToastService.Show(this, $"无法加入画板：{ex.Message}");
        }
    }
}
