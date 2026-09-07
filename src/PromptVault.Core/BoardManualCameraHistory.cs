namespace PromptVault.Core;

/// <summary>
/// Keeps the most recent completed user pan/zoom as a two-way, one-level camera toggle.
/// Focus/fit/reset camera sessions deliberately use a separate controller.
/// </summary>
public sealed class BoardManualCameraHistory
{
    private BoardViewport? _before;
    private BoardViewport? _after;
    private bool _showingBefore;

    public bool CanToggle => _before is not null && _after is not null;

    public void Record(BoardViewport before, BoardViewport after)
    {
        if (!Changed(before, after)) return;
        _before = before;
        _after = after;
        _showingBefore = false;
    }

    public BoardViewport? Toggle(double viewportWidth, double viewportHeight)
    {
        if (_before is not { } before || _after is not { } after) return null;
        _showingBefore = !_showingBefore;
        var target = _showingBefore ? before : after;
        return target with
        {
            Width = Math.Max(1, viewportWidth),
            Height = Math.Max(1, viewportHeight)
        };
    }

    public void Reset()
    {
        _before = null;
        _after = null;
        _showingBefore = false;
    }

    private static bool Changed(BoardViewport left, BoardViewport right) =>
        Math.Abs(left.OffsetX - right.OffsetX) > 0.001
        || Math.Abs(left.OffsetY - right.OffsetY) > 0.001
        || Math.Abs(left.Zoom - right.Zoom) > 0.000_001;
}
