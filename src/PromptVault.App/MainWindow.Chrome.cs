using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class MainWindow
{
    private bool _oldestFirst;
    private readonly Stopwatch _edgeIntentClock = Stopwatch.StartNew();
    private readonly EdgeIntentDetector _edgeIntentDetector = new();
    private readonly DispatcherTimer _edgeIntentTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _topHideTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly DispatcherTimer _leftHideTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private const double HiddenPanelVisibleEdge = 6d;
    private const double TopPanelCollapsedOffsetMinimum = 130d;
    private const double LeftPanelCollapsedOffsetMinimum = 180d;
    private const double TopPanelMouseSafetyMargin = 4d;
    private static readonly TimeSpan SafeCorridorDuration = TimeSpan.FromMilliseconds(900);
    private TimeSpan _topSafeCorridorUntil;
    private TimeSpan _leftSafeCorridorUntil;
    private bool _topPanelRevealed;
    private bool _leftPanelRevealed;
    private bool _leftPanelMenuOpen;
    private bool EdgeMenusEffectivelyAlwaysVisible =>
        _settings.EdgeMenusAlwaysVisible && !_transparentMode;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!_trueTransparentWindow) BackdropService.Apply(this);
        _edgeIntentTimer.Tick += (_, _) => PollEdgeIntent();
        _topHideTimer.Tick += (_, _) =>
        {
            _topHideTimer.Stop();
            if (EdgeMenusEffectivelyAlwaysVisible) return;
            if (IsMouseInsideTopPanel() || IsInsideTopSafeCorridor())
            {
                _topHideTimer.Interval = TimeSpan.FromMilliseconds(120);
                _topHideTimer.Start();
                return;
            }
            HideTopPanel();
        };
        _leftHideTimer.Tick += (_, _) =>
        {
            _leftHideTimer.Stop();
            if (EdgeMenusEffectivelyAlwaysVisible) return;
            if (_leftPanelMenuOpen || IsMouseInsideLeftPanel() || IsInsideLeftSafeCorridor())
            {
                _leftHideTimer.Interval = TimeSpan.FromMilliseconds(120);
                _leftHideTimer.Start();
                return;
            }
            HideLeftPanel();
        };
        StateChanged += (_, _) => WindowSurface.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(14);
        Loaded += (_, _) =>
        {
            UpdateTopPanelHeight();
            ApplyEdgeMenuPreference();
        };
    }

    private void TopPanelMouseEnter(object sender, MouseEventArgs e)
    {
        _topHideTimer.Stop();
        if (!_topPanelRevealed && !EdgeMenusEffectivelyAlwaysVisible)
        {
            ObserveEdgeIntent(e.GetPosition(this));
            return;
        }
        _edgeIntentDetector.Reset();
        UpdateTopPanelHeight();
        ShowTopPanel();
    }

    private void TopPanelMouseLeave(object sender, MouseEventArgs e)
    {
        if (EdgeMenusEffectivelyAlwaysVisible) return;
        _topHideTimer.Stop();
        _topHideTimer.Interval = TimeSpan.FromMilliseconds(280);
        _topHideTimer.Start();
    }

    private bool IsMouseInsideTopPanel() => IsMouseInsideElementBounds(TopPanel);

    private bool IsMouseInsideLeftPanel() => IsMouseInsideElementBounds(LeftPanel);

    private static bool IsMouseInsideElementBounds(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;

        var cursor = System.Windows.Forms.Cursor.Position;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        var left = Math.Min(topLeft.X, bottomRight.X) - TopPanelMouseSafetyMargin;
        var right = Math.Max(topLeft.X, bottomRight.X) + TopPanelMouseSafetyMargin;
        var top = Math.Min(topLeft.Y, bottomRight.Y) - TopPanelMouseSafetyMargin;
        var bottom = Math.Max(topLeft.Y, bottomRight.Y) + TopPanelMouseSafetyMargin;

        return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
    }

    private void LeftPanelMouseEnter(object sender, MouseEventArgs e)
    {
        _leftHideTimer.Stop();
        if (!_leftPanelRevealed && !EdgeMenusEffectivelyAlwaysVisible)
        {
            ObserveEdgeIntent(e.GetPosition(this));
            return;
        }
        _edgeIntentDetector.Reset();
        ShowLeftPanel();
    }

    private void LeftPanelMouseLeave(object sender, MouseEventArgs e)
    {
        if (EdgeMenusEffectivelyAlwaysVisible) return;
        _leftHideTimer.Stop();
        _leftHideTimer.Interval = TimeSpan.FromMilliseconds(280);
        if (!_leftPanelMenuOpen) _leftHideTimer.Start();
    }

    private void TopRevealMouseEnter(object sender, MouseEventArgs e) => ObserveEdgeIntent(e.GetPosition(this));
    private void TopRevealMouseLeave(object sender, MouseEventArgs e) => ObserveEdgeIntent(e.GetPosition(this));
    private void LeftRevealMouseEnter(object sender, MouseEventArgs e) => ObserveEdgeIntent(e.GetPosition(this));
    private void LeftRevealMouseLeave(object sender, MouseEventArgs e) => ObserveEdgeIntent(e.GetPosition(this));

    private void ObserveEdgeIntent(Point position)
    {
        if (EdgeMenusEffectivelyAlwaysVisible || ImmersiveViewer.Visibility == Visibility.Visible)
        {
            _edgeIntentDetector.Reset();
            _edgeIntentTimer.Stop();
            return;
        }

        var profile = EdgeIntentProfile.FromSensitivity(_settings.EdgeMenuSensitivity);
        var atTrackableLeftEdge = position.X <= profile.ActivationBand && !_leftPanelRevealed;
        var atTrackableTopEdge = position.Y <= profile.ActivationBand && !_topPanelRevealed;
        if (!atTrackableLeftEdge && !atTrackableTopEdge)
        {
            _edgeIntentDetector.Reset();
            _edgeIntentTimer.Stop();
            return;
        }
        var sampleX = _leftPanelRevealed ? profile.ActivationBand + 1 : position.X;
        var sampleY = _topPanelRevealed ? profile.ActivationBand + 1 : position.Y;
        _edgeIntentDetector.Observe(
            sampleX,
            sampleY,
            ActualWidth,
            ActualHeight,
            _edgeIntentClock.Elapsed,
            profile);
        if (_edgeIntentDetector.Candidate == EdgeIntentEdge.None) _edgeIntentTimer.Stop();
        else if (!_edgeIntentTimer.IsEnabled) _edgeIntentTimer.Start();
    }

    private void PollEdgeIntent()
    {
        if (EdgeMenusEffectivelyAlwaysVisible || ImmersiveViewer.Visibility == Visibility.Visible)
        {
            _edgeIntentDetector.Reset();
            _edgeIntentTimer.Stop();
            return;
        }

        var now = _edgeIntentClock.Elapsed;
        var profile = EdgeIntentProfile.FromSensitivity(_settings.EdgeMenuSensitivity);
        var position = Mouse.GetPosition(this);
        var atTrackableLeftEdge = position.X <= profile.ActivationBand && !_leftPanelRevealed;
        var atTrackableTopEdge = position.Y <= profile.ActivationBand && !_topPanelRevealed;
        if (!atTrackableLeftEdge && !atTrackableTopEdge)
        {
            _edgeIntentDetector.Reset();
            _edgeIntentTimer.Stop();
            return;
        }
        var sampleX = _leftPanelRevealed ? profile.ActivationBand + 1 : position.X;
        var sampleY = _topPanelRevealed ? profile.ActivationBand + 1 : position.Y;
        var edge = _edgeIntentDetector.Poll(
            sampleX,
            sampleY,
            ActualWidth,
            ActualHeight,
            now,
            profile);
        if (_edgeIntentDetector.Candidate == EdgeIntentEdge.None)
        {
            _edgeIntentTimer.Stop();
            return;
        }
        if (edge == EdgeIntentEdge.None) return;

        var dwell = _edgeIntentDetector.CandidateDwell(now).TotalMilliseconds;
        _edgeIntentDetector.Reset();
        _edgeIntentTimer.Stop();
        RevealEdgePanel(edge);
        DevelopmentPerformanceTrace.Event("edge-intent-reveal", new
        {
            edge = edge.ToString(),
            dwellMs = Math.Round(dwell, 1),
            sensitivity = _settings.EdgeMenuSensitivity,
            alwaysVisible = _settings.EdgeMenusAlwaysVisible
        });
    }

    private void RevealEdgePanel(EdgeIntentEdge edge)
    {
        if (edge == EdgeIntentEdge.Top)
        {
            UpdateTopPanelHeight();
            _topSafeCorridorUntil = _edgeIntentClock.Elapsed + SafeCorridorDuration;
            ShowTopPanel();
        }
        else if (edge == EdgeIntentEdge.Left)
        {
            _leftSafeCorridorUntil = _edgeIntentClock.Elapsed + SafeCorridorDuration;
            ShowLeftPanel();
        }
    }

    private bool IsInsideTopSafeCorridor()
    {
        if (_edgeIntentClock.Elapsed >= _topSafeCorridorUntil) return false;
        var position = Mouse.GetPosition(this);
        return position.X >= -8 && position.X <= ActualWidth + 8
            && position.Y >= -8 && position.Y <= TopPanel.ActualHeight + 36;
    }

    private bool IsInsideLeftSafeCorridor()
    {
        if (_edgeIntentClock.Elapsed >= _leftSafeCorridorUntil) return false;
        var position = Mouse.GetPosition(this);
        return position.Y >= -8 && position.Y <= ActualHeight + 8
            && position.X >= -8 && position.X <= LeftPanel.ActualWidth + 36;
    }

    private void EdgeSensitivityClick(object sender, RoutedEventArgs e)
    {
        _settings.EdgeMenuSensitivity = EdgeIntentProfile.NormalizeSensitivity(_settings.EdgeMenuSensitivity) switch
        {
            EdgeIntentProfile.LowSensitivity => EdgeIntentProfile.NormalSensitivity,
            EdgeIntentProfile.NormalSensitivity => EdgeIntentProfile.HighSensitivity,
            _ => EdgeIntentProfile.LowSensitivity
        };
        _settings.Save();
        UpdateEdgeMenuVisuals();
    }

    private void EdgeAlwaysVisibleClick(object sender, RoutedEventArgs e)
    {
        _settings.EdgeMenusAlwaysVisible = !_settings.EdgeMenusAlwaysVisible;
        _settings.Save();
        ApplyEdgeMenuPreference();
    }

    private void ApplyEdgeMenuPreference()
    {
        UpdateEdgeMenuVisuals();
        _edgeIntentDetector.Reset();
        _edgeIntentTimer.Stop();
        _topHideTimer.Stop();
        _leftHideTimer.Stop();
        if (EdgeMenusEffectivelyAlwaysVisible)
        {
            UpdateTopPanelHeight();
            ShowTopPanel();
            ShowLeftPanel();
        }
        else
        {
            HideTopPanel();
            HideLeftPanel();
        }
    }

    private void UpdateEdgeMenuVisuals()
    {
        if (EdgeSensitivityButton is null || EdgeAlwaysVisibleButton is null) return;
        EdgeSensitivityButton.Content = EdgeIntentProfile.NormalizeSensitivity(_settings.EdgeMenuSensitivity) switch
        {
            EdgeIntentProfile.LowSensitivity => "边缘：低",
            EdgeIntentProfile.HighSensitivity => "边缘：高",
            _ => "边缘：标准"
        };
        EdgeAlwaysVisibleButton.Content = _settings.EdgeMenusAlwaysVisible ? "边栏：常显" : "边栏：智能";
        EdgeAlwaysVisibleButton.Background = _settings.EdgeMenusAlwaysVisible
            ? VisualModeService.ResourceBrush("AccentSoftBrush")
            : VisualModeService.ResourceBrush("ButtonSurfaceBrush");
    }

    private void TopControlsWrapSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTopPanelHeight();
    private void HoldLeftPanelForMenu(System.Windows.Controls.ContextMenu menu)
    {
        _leftPanelMenuOpen = true;
        _leftHideTimer.Stop();
        ShowLeftPanel();
        menu.Closed -= SidebarContextMenuClosed;
        menu.Closed += SidebarContextMenuClosed;
    }

    private void SidebarContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        _leftPanelMenuOpen = false;
        _leftHideTimer.Stop();
        if (!LeftPanel.IsMouseOver) _leftHideTimer.Start();
    }

    private void UpdateTopPanelHeight()
    {
        var rows = Math.Clamp((int)Math.Ceiling(Math.Max(58, TopControlsWrap.ActualHeight) / 58d), 1, 3);
        var target = rows switch { 1 => 136d, 2 => 196d, _ => 254d };
        if (Math.Abs(TopPanel.Height - target) < 0.5) return;
        TopPanel.Height = target;
        if (TopPanelTransform.Y < 0 && !IsMouseInsideTopPanel()) SetTopPanelHiddenPosition();
    }

    private void StabilizeHiddenPanelsForResize()
    {
        if (EdgeMenusEffectivelyAlwaysVisible)
        {
            UpdateTopPanelHeight();
            ShowTopPanel();
            ShowLeftPanel();
            return;
        }

        if (!IsMouseInsideTopPanel())
        {
            _topHideTimer.Stop();
            SetTopPanelHiddenPosition();
        }

        if (!_leftPanelMenuOpen && !IsMouseInsideLeftPanel())
        {
            _leftHideTimer.Stop();
            SetLeftPanelHiddenPosition();
        }
    }

    private double TopPanelHiddenOffset()
    {
        var height = TopPanel.Height > 0 ? TopPanel.Height : TopPanel.ActualHeight;
        return -Math.Max(TopPanelCollapsedOffsetMinimum, height - HiddenPanelVisibleEdge);
    }

    private double LeftPanelHiddenOffset()
    {
        var width = LeftPanel.ActualWidth > 0 ? LeftPanel.ActualWidth : LeftPanel.Width;
        return -Math.Max(LeftPanelCollapsedOffsetMinimum, width - HiddenPanelVisibleEdge);
    }

    private void SetTopPanelHiddenPosition()
    {
        _topPanelRevealed = false;
        TopPanelTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        TopPanelTransform.Y = TopPanelHiddenOffset();
    }

    private void SetLeftPanelHiddenPosition()
    {
        _leftPanelRevealed = false;
        LeftPanelTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        LeftPanelTransform.X = LeftPanelHiddenOffset();
    }

    private void ShowTopPanel()
    {
        _topPanelRevealed = true;
        AnimateTop(0);
    }

    private void ShowLeftPanel()
    {
        _leftPanelRevealed = true;
        AnimateLeft(0);
    }

    private void HideTopPanel()
    {
        _topPanelRevealed = false;
        AnimateTop(TopPanelHiddenOffset());
    }

    private void HideLeftPanel()
    {
        _leftPanelRevealed = false;
        AnimateLeft(LeftPanelHiddenOffset());
    }
    private void AnimateTop(double value) => TopPanelTransform.BeginAnimation(
        System.Windows.Media.TranslateTransform.YProperty, PanelAnimation(value));

    private void AnimateLeft(double value) => LeftPanelTransform.BeginAnimation(
        System.Windows.Media.TranslateTransform.XProperty, PanelAnimation(value));

    private static DoubleAnimation PanelAnimation(double value) => new(value, VisualModeService.Motion(MotionToken.Panel))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.HoldEnd
    };

    private void DragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (WindowState == WindowState.Normal)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private async void SortButtonClick(object sender, RoutedEventArgs e)
    {
        _oldestFirst = !_oldestFirst;
        SortButton.Content = _oldestFirst ? "最早优先  ↕" : "最新优先  ↕";
        await RefreshAsync(RefreshAnimationKind.ViewSwitch);
    }

    private void PinButtonClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        if (PinButton is null) return;
        PinButton.Content = Topmost ? "已置顶" : "置顶";
        PinButton.Background = Topmost
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 118, 153))
            : System.Windows.Media.Brushes.Transparent;
    }
    private void MinimizeButtonClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButtonClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButtonClick(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
