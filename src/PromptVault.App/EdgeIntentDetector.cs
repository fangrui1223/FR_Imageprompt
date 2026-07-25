namespace PromptVault.App;

public enum EdgeIntentEdge
{
    None,
    Top,
    Left
}

public readonly record struct EdgeIntentProfile(
    TimeSpan Dwell,
    double MaximumApproachSpeed,
    double ActivationBand)
{
    public const string LowSensitivity = "Low";
    public const string NormalSensitivity = "Normal";
    public const string HighSensitivity = "High";

    public static EdgeIntentProfile FromSensitivity(string? sensitivity) => NormalizeSensitivity(sensitivity) switch
    {
        LowSensitivity => new(TimeSpan.FromMilliseconds(140), 720, 22),
        HighSensitivity => new(TimeSpan.FromMilliseconds(90), 1280, 22),
        _ => new(TimeSpan.FromMilliseconds(115), 960, 22)
    };

    public static string NormalizeSensitivity(string? sensitivity) => sensitivity switch
    {
        LowSensitivity => LowSensitivity,
        HighSensitivity => HighSensitivity,
        _ => NormalSensitivity
    };
}

public sealed class EdgeIntentDetector
{
    private static readonly TimeSpan StationaryQualification = TimeSpan.FromMilliseconds(34);
    private EdgeIntentEdge _candidate;
    private TimeSpan _candidateSince;
    private TimeSpan? _stationarySince;
    private TimeSpan _lastTime;
    private double _lastX;
    private double _lastY;
    private double _lastDistance;
    private double _lastSpeed = double.PositiveInfinity;
    private bool _hasLastSample;
    private bool _qualified;

    public EdgeIntentEdge Candidate => _candidate;

    public TimeSpan CandidateDwell(TimeSpan now) =>
        _candidate == EdgeIntentEdge.None ? TimeSpan.Zero : Max(TimeSpan.Zero, now - _candidateSince);

    public void Observe(
        double x,
        double y,
        double width,
        double height,
        TimeSpan now,
        EdgeIntentProfile profile)
    {
        var edge = DetectEdge(x, y, width, height, profile.ActivationBand);
        var distance = EdgeDistance(edge, x, y);
        var elapsed = _hasLastSample ? now - _lastTime : TimeSpan.Zero;
        var speed = elapsed > TimeSpan.Zero
            ? Math.Sqrt(Math.Pow(x - _lastX, 2) + Math.Pow(y - _lastY, 2)) / elapsed.TotalSeconds
            : double.PositiveInfinity;
        var movingTowardEdge = _hasLastSample && edge != EdgeIntentEdge.None && distance <= _lastDistance;

        if (edge == EdgeIntentEdge.None)
        {
            ClearCandidate();
        }
        else if (edge != _candidate)
        {
            _candidate = edge;
            _candidateSince = now;
            _stationarySince = null;
            _qualified = false;
        }
        else
        {
            if (speed <= 24)
            {
                _stationarySince ??= now;
                if (now - _stationarySince >= StationaryQualification)
                {
                    _qualified = true;
                }
            }
            else
            {
                _stationarySince = null;
            }

            var decelerating = speed <= _lastSpeed * 0.82 || speed <= profile.MaximumApproachSpeed * 0.45;
            if (movingTowardEdge && speed <= profile.MaximumApproachSpeed && decelerating)
            {
                _qualified = true;
            }
        }

        _hasLastSample = true;
        _lastX = x;
        _lastY = y;
        _lastTime = now;
        _lastDistance = edge == EdgeIntentEdge.None ? double.PositiveInfinity : distance;
        _lastSpeed = speed;
    }

    public EdgeIntentEdge Poll(
        double x,
        double y,
        double width,
        double height,
        TimeSpan now,
        EdgeIntentProfile profile)
    {
        Observe(x, y, width, height, now, profile);
        return _candidate != EdgeIntentEdge.None
            && _qualified
            && now - _candidateSince >= profile.Dwell
                ? _candidate
                : EdgeIntentEdge.None;
    }

    public void Reset()
    {
        ClearCandidate();
        _hasLastSample = false;
        _lastSpeed = double.PositiveInfinity;
    }

    private void ClearCandidate()
    {
        _candidate = EdgeIntentEdge.None;
        _stationarySince = null;
        _qualified = false;
    }

    private static EdgeIntentEdge DetectEdge(
        double x,
        double y,
        double width,
        double height,
        double activationBand)
    {
        if (width <= 0 || height <= 0 || x < -activationBand || y < -activationBand
            || x > width + activationBand || y > height + activationBand)
        {
            return EdgeIntentEdge.None;
        }

        var atLeft = x <= activationBand;
        var atTop = y <= activationBand;
        if (atLeft && atTop) return x <= y ? EdgeIntentEdge.Left : EdgeIntentEdge.Top;
        if (atLeft) return EdgeIntentEdge.Left;
        return atTop ? EdgeIntentEdge.Top : EdgeIntentEdge.None;
    }

    private static double EdgeDistance(EdgeIntentEdge edge, double x, double y) => edge switch
    {
        EdgeIntentEdge.Top => Math.Max(0, y),
        EdgeIntentEdge.Left => Math.Max(0, x),
        _ => double.PositiveInfinity
    };

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;
}
