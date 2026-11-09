namespace AresToys.App.Views;

/// <summary>Turns mouse wheel deltas into whole steps, one per 120 units (WHEEL_DELTA). A mouse
/// notch is exactly one step; a precision touchpad, which sends many small deltas (3 to 30) per
/// gesture, has to scroll the same distance before a step fires instead of stepping on every
/// event. The remainder is dropped when the direction flips or after a short idle, so a new
/// gesture never inherits half a step from the previous one. One instance per action.</summary>
public sealed class WheelStepAccumulator
{
    public const int WheelDelta = 120;

    public static readonly TimeSpan IdleReset = TimeSpan.FromMilliseconds(300);

    private int _pending;
    private TimeSpan _last;
    private bool _hasLast;

    /// <summary>Feed one wheel event, get the signed number of steps to apply now (often 0 on a
    /// touchpad). <paramref name="now"/> is any monotonic clock.</summary>
    public int Add(int delta, TimeSpan now)
    {
        if (delta == 0) return 0;
        if (!_hasLast || now - _last > IdleReset || Math.Sign(delta) != Math.Sign(_pending))
            _pending = 0;
        _last = now;
        _hasLast = true;
        _pending += delta;
        var steps = _pending / WheelDelta;
        _pending -= steps * WheelDelta;
        return steps;
    }

    /// <summary><see cref="Add(int, TimeSpan)"/> on the system tick clock.</summary>
    public int Add(int delta) => Add(delta, TimeSpan.FromMilliseconds(Environment.TickCount64));
}
