using System.Globalization;
using System.Text;

namespace AresToys.Capture.Recording;

public enum RecordingFormat { Mp4, Gif }

public sealed record RecordingOptions(
    int X,
    int Y,
    int Width,
    int Height,
    int Fps,
    bool DrawCursor,
    string OutputPath,
    RecordingFormat Format);

/// <summary>Pure FFmpeg command-line builder. Mirrors the gdigrab path of ShareX's
/// ScreenRecordingOptions: gdigrab input + libx264 (mp4) or libwebp (animated webp/gif) encoder.
/// Kept logic-only so it can be unit-tested without touching the filesystem.</summary>
public static class FfmpegArgsBuilder
{
    public static string Build(RecordingOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var sb = new StringBuilder();

        // H.264 with yuv420p requires both dimensions to be even (it subsamples chroma 2×2). If the
        // user picked an odd-sized region, libx264 fails to open the encoder and writes 0 frames.
        // Truncate down to the nearest even pair for mp4 — gif via palette filter is fine with odd.
        var (w, h) = o.Format == RecordingFormat.Mp4
            ? (o.Width & ~1, o.Height & ~1)
            : (o.Width, o.Height);

        // Input — gdigrab is built into FFmpeg, no external driver required.
        sb.Append("-f gdigrab ");
        sb.Append("-thread_queue_size 1024 ");
        sb.Append("-rtbufsize 256M ");
        sb.Append(CultureInfo.InvariantCulture, $"-framerate {o.Fps} ");
        sb.Append(CultureInfo.InvariantCulture, $"-offset_x {o.X} ");
        sb.Append(CultureInfo.InvariantCulture, $"-offset_y {o.Y} ");
        sb.Append(CultureInfo.InvariantCulture, $"-video_size {w}x{h} ");
        sb.Append(CultureInfo.InvariantCulture, $"-draw_mouse {(o.DrawCursor ? 1 : 0)} ");
        sb.Append("-i desktop ");

        // Encoder
        if (o.Format == RecordingFormat.Mp4)
        {
            sb.Append("-c:v libx264 ");
            sb.Append(CultureInfo.InvariantCulture, $"-r {o.Fps} ");
            sb.Append("-preset veryfast ");
            sb.Append("-tune zerolatency ");
            sb.Append("-crf 23 ");
            sb.Append("-pix_fmt yuv420p ");
            // No -movflags +faststart: that re-writes the entire file at finalize time to move the
            // moov atom to the start, which can take many seconds on long recordings — long enough
            // to make stop look like it's hanging and risk a forced kill that leaves the file at 0KB.
            // For local playback (the only use case here) the moov-at-end mp4 is just as valid.
        }
        else // Gif — real GIF with a generated palette (split → palettegen → paletteuse), the standard
             // recipe for decent-quality animated GIFs. Without it FFmpeg's gif encoder picks a static
             // 256-color palette per frame and the output looks awful.
        {
            sb.Append("-vf \"split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" ");
            sb.Append(CultureInfo.InvariantCulture, $"-r {o.Fps} ");
            sb.Append("-loop 0 ");
        }

        sb.Append("-y "); // overwrite
        sb.Append(CultureInfo.InvariantCulture, $"\"{o.OutputPath}\"");
        return sb.ToString();
    }
}
