namespace AresToys.App.Services.Peek;

/// <summary>How Peek fits an image it draws itself into the preview area.</summary>
public enum PeekZoomMode
{
    /// <summary>100% when the image fits, else shrunk to fit.</summary>
    Auto,

    /// <summary>Scaled to fit, enlarging a small image too.</summary>
    Fit,

    /// <summary>Scaled to cover the whole area (the overflow is cropped, drag to pan).</summary>
    Fill,

    /// <summary>Scaled so the width fits.</summary>
    Width,

    /// <summary>Scaled so the height fits.</summary>
    Height,

    /// <summary>The scale of the previous file is kept.</summary>
    Lock,
}

/// <summary>The zoom modes: the text of the saved value, the display names and the scale function. The value itself is
/// saved by <see cref="PeekWindowStore"/> under <see cref="PeekWindowStore.ZoomKey"/>.</summary>
internal static class PeekZoom
{
    /// <summary>The smallest and the largest scale Ctrl+wheel reaches (2% and 3200%).</summary>
    public const double MinCustomScale = 0.02;
    public const double MaxCustomScale = 32;

    /// <summary>The factor one wheel notch multiplies the scale by (a notch down divides by it).</summary>
    public const double WheelStep = 1.2;

    /// <summary><paramref name="scale"/> moved by <paramref name="notches"/> wheel notches (negative: out), kept between
    /// <see cref="MinCustomScale"/> and <see cref="MaxCustomScale"/>.</summary>
    public static double Step(double scale, double notches)
        => Math.Clamp(scale * Math.Pow(WheelStep, notches), MinCustomScale, MaxCustomScale);

    public static readonly PeekZoomMode[] All =
    [
        PeekZoomMode.Auto, PeekZoomMode.Fit, PeekZoomMode.Fill, PeekZoomMode.Width, PeekZoomMode.Height, PeekZoomMode.Lock,
    ];

    /// <summary>The text stored for <paramref name="mode"/>.</summary>
    public static string Format(PeekZoomMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>The mode stored as <paramref name="raw"/>; <see cref="PeekZoomMode.Auto"/> for anything unknown.</summary>
    public static PeekZoomMode Parse(string? raw)
    {
        foreach (var mode in All)
        {
            if (string.Equals(Format(mode), raw, StringComparison.Ordinal)) return mode;
        }

        return PeekZoomMode.Auto;
    }

    public static string DisplayName(PeekZoomMode mode) => mode switch
    {
        PeekZoomMode.Fit => AresToys.App.Resources.Strings.Peek_ZoomFit,
        PeekZoomMode.Fill => AresToys.App.Resources.Strings.Peek_ZoomFill,
        PeekZoomMode.Width => AresToys.App.Resources.Strings.Peek_ZoomWidth,
        PeekZoomMode.Height => AresToys.App.Resources.Strings.Peek_ZoomHeight,
        PeekZoomMode.Lock => AresToys.App.Resources.Strings.Peek_ZoomLock,
        _ => AresToys.App.Resources.Strings.Peek_ZoomAuto,
    };

    /// <summary>The factor (1 = one image pixel per screen pixel) that shows an image of
    /// <paramref name="imageWidth"/> x <paramref name="imageHeight"/> pixels (as displayed, rotation applied) in an area
    /// of <paramref name="areaWidth"/> x <paramref name="areaHeight"/> pixels. <paramref name="areaWidth"/> and
    /// <paramref name="areaHeight"/> are the room Fit, Width and Height use; <paramref name="coverWidth"/> and
    /// <paramref name="coverHeight"/> the area Fill covers. <paramref name="lockScale"/> is the factor Lock keeps (null: it acts as Auto).</summary>
    public static double Scale(PeekZoomMode mode, double imageWidth, double imageHeight, double areaWidth, double areaHeight,
        double coverWidth, double coverHeight, double? lockScale)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || areaWidth <= 0 || areaHeight <= 0)
        {
            return 1;
        }

        var byWidth = areaWidth / imageWidth;
        var byHeight = areaHeight / imageHeight;
        var fit = Math.Min(byWidth, byHeight);
        return mode switch
        {
            PeekZoomMode.Fit => fit,
            PeekZoomMode.Fill => Math.Max(coverWidth / imageWidth, coverHeight / imageHeight),
            PeekZoomMode.Width => byWidth,
            PeekZoomMode.Height => byHeight,
            PeekZoomMode.Lock when lockScale is { } locked && locked > 0 => locked,
            _ => Math.Min(1, fit),
        };
    }
}
