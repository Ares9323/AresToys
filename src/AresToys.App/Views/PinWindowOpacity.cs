namespace AresToys.App.Views;

/// <summary>Whole-window opacity for the pinned image / video windows (issue #26). Both pins run
/// with <c>AllowsTransparency="True"</c> so <see cref="System.Windows.Window.Opacity"/> takes
/// effect: a constant alpha set from outside through <c>SetLayeredWindowAttributes</c> on a
/// hardware-rendered WPF window had no visible effect.</summary>
internal static class PinWindowOpacity
{
    public const double Min = 0.1;
    public const double Step = 0.05;

    /// <summary>Next opacity for one wheel notch, snapped to the 5% grid.</summary>
    public static double Next(double current, int wheelDelta)
    {
        var next = current + (wheelDelta > 0 ? Step : -Step);
        return Math.Clamp(Math.Round(next / Step) * Step, Min, 1.0);
    }

    /// <summary>Opacity after <paramref name="steps"/> wheel steps (negative = more transparent).</summary>
    public static double Steps(double current, int steps)
    {
        for (var i = 0; i < Math.Abs(steps); i++) current = Next(current, steps);
        return current;
    }

    /// <summary>A persisted opacity brought back into range (hand-edited or corrupt manifest).</summary>
    public static double Clamp(double opacity)
        => double.IsFinite(opacity) ? Math.Clamp(opacity, Min, 1.0) : 1.0;

    public static string Format(double opacity) => $"{Math.Round(opacity * 100)}%";
}
