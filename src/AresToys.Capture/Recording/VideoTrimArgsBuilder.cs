using System.Globalization;

namespace AresToys.Capture.Recording;

/// <summary>A trim request: which file, where to write it, and the window to keep.</summary>
public sealed record VideoTrimOptions(string InputPath, string OutputPath, TimeSpan Start, TimeSpan End);

/// <summary>Builds the FFmpeg command that cuts a recording down to a chosen window, and keeps the
/// selection sane. Logic-only, like <see cref="FfmpegArgsBuilder"/>, so it can be tested without a
/// process or a file.
///
/// <para><b>Why it re-encodes instead of copying the stream.</b> A stream copy is tempting: it's
/// ~7× faster and lossless. Measured on this app's own recordings, though, the encoder emits a
/// keyframe only every 8.3 seconds (nothing sets <c>-g</c>, so x264's default 250-frame GOP
/// applies), and FFmpeg's copy cut is frame-accurate only because it keeps the frames before the
/// cut and writes an MP4 <i>edit list</i> telling the player where to start. A player that ignores
/// that metadata replays up to a whole GOP the user believed was cut: a 5.5-second trim measured
/// 13.67 seconds when read with <c>-ignore_editlist</c>. Re-encoding removes the dependency on a
/// metadata box for correctness, and it costs about half a second for a ten-second window, so the
/// trade isn't close.</para></summary>
public static class VideoTrimArgsBuilder
{
    /// <summary>Containers this command handles: H.264 in an ISO-BMFF container, which covers
    /// everything the recorder produces in video mode. GIF (needs the palettegen/paletteuse
    /// pipeline) and WebM (needs VP9, far slower to encode) each want a different command, and the
    /// other formats the preview recognises are ones this app never writes.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    /// <summary>Shortest window a trim may produce. A zero-length selection would encode an empty
    /// file, which reads as a failure to the user; one frame at 30 fps is 33 ms, so 100 ms is
    /// comfortably above "something is there" without being a selection anyone aimed for.</summary>
    public static readonly TimeSpan MinimumSelection = TimeSpan.FromMilliseconds(100);

    public static bool IsSupported(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext;
        try { ext = System.IO.Path.GetExtension(path); }
        catch { return false; }
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    /// <summary>Bring a selection inside the clip: order the two handles, pull them within
    /// [0, duration], and widen a degenerate selection to <see cref="MinimumSelection"/> (backwards
    /// from the end when there's no room after it).</summary>
    public static (TimeSpan Start, TimeSpan End) Clamp(TimeSpan start, TimeSpan end, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return (TimeSpan.Zero, MinimumSelection);

        if (end < start) (start, end) = (end, start);
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (end > duration) end = duration;
        if (start > duration) start = duration;

        if (end - start < MinimumSelection)
        {
            // Grow forwards if the clip allows it, otherwise backwards from the end.
            if (start + MinimumSelection <= duration) end = start + MinimumSelection;
            else
            {
                end = duration;
                start = duration - MinimumSelection;
                if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            }
        }
        return (start, end);
    }

    /// <summary>True when the selection still spans the whole clip, i.e. there is nothing to cut.
    /// Callers use it to keep the confirm button disabled: re-encoding a clip into an identical
    /// clip only spends a generation of quality.</summary>
    public static bool IsWholeClip(TimeSpan start, TimeSpan end, TimeSpan duration) =>
        start <= TimeSpan.Zero && end >= duration;

    public static string Build(VideoTrimOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        // Both -ss and -to ahead of -i. Measured frame-accurate in this form once re-encoding:
        // the first output frame matched the source frame at the requested time exactly.
        return string.Create(CultureInfo.InvariantCulture,
            $"-y -ss {Seconds(o.Start)} -to {Seconds(o.End)} -i \"{o.InputPath}\" "
            + $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p \"{o.OutputPath}\"");
    }

    /// <summary>Seconds with a dot, whatever the thread's culture. Under it-IT the default
    /// formatting yields "1,5", which FFmpeg reads as 1 second and silently drops the fraction.</summary>
    private static string Seconds(TimeSpan t) =>
        t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
