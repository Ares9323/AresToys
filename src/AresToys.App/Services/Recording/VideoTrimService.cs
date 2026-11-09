using System.Diagnostics;
using System.IO;
using AresToys.Capture.Recording;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Recording;

/// <summary>Cuts a recording down to a chosen window by running FFmpeg. The command itself is built
/// by <see cref="VideoTrimArgsBuilder"/> (pure, tested); this type owns finding the binary, running
/// it and picking a destination that doesn't overwrite anything.
///
/// Never touches the input: the result is a new file, so the clipboard entry the user trimmed from
/// stays intact and the trim stays undoable by simply keeping the original.</summary>
public sealed class VideoTrimService
{
    private readonly FfmpegLocator _ffmpeg;
    private readonly ILogger<VideoTrimService> _logger;

    public VideoTrimService(FfmpegLocator ffmpeg, ILogger<VideoTrimService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>Whether this path is something the trim can act on at all. Thin pass-through so
    /// callers (the toolbar button's visibility, the pipeline task) don't each re-derive the
    /// supported list.</summary>
    public static bool CanTrim(string? path) => VideoTrimArgsBuilder.IsSupported(path);

    /// <summary>Trim <paramref name="inputPath"/> to the window between <paramref name="start"/>
    /// and <paramref name="end"/>, writing a sibling file. Returns its path, or null when ffmpeg is
    /// missing, the input isn't a supported container, or the encode failed — every failure is
    /// logged, none throws, because every caller here is UI that has to stay standing.</summary>
    public async Task<string?> TrimAsync(string inputPath, TimeSpan start, TimeSpan end,
        TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!VideoTrimArgsBuilder.IsSupported(inputPath))
        {
            _logger.LogWarning("VideoTrimService: {Path} is not a trimmable container", inputPath);
            return null;
        }
        if (!File.Exists(inputPath))
        {
            _logger.LogWarning("VideoTrimService: {Path} no longer exists", inputPath);
            return null;
        }

        var ffmpeg = _ffmpeg.Find();
        if (ffmpeg is null)
        {
            _logger.LogWarning("VideoTrimService: ffmpeg.exe not found — cannot trim {Path}", inputPath);
            return null;
        }

        var (from, to) = VideoTrimArgsBuilder.Clamp(start, end, duration);
        var outputPath = BuildOutputPath(inputPath);
        var args = VideoTrimArgsBuilder.Build(new VideoTrimOptions(inputPath, outputPath, from, to));

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            // Drain both pipes: ffmpeg writes its progress to stderr and a full pipe would deadlock
            // the encode. Same handling as SaveVideoFileTask.
            _ = p.StandardError.ReadToEndAsync(cancellationToken);
            _ = p.StandardOutput.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (p.ExitCode != 0)
            {
                _logger.LogWarning("VideoTrimService: ffmpeg exited {Code} for args: {Args}", p.ExitCode, args);
                TryDelete(outputPath);
                return null;
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                _logger.LogWarning("VideoTrimService: ffmpeg produced no output for {Path}", inputPath);
                TryDelete(outputPath);
                return null;
            }

            _logger.LogInformation("VideoTrimService: trimmed {In} [{From} → {To}] into {Out}",
                inputPath, from, to, outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VideoTrimService: trim failed for {Path}", inputPath);
            TryDelete(outputPath);
            return null;
        }
    }

    /// <summary>Sibling of the input named "<c>&lt;name&gt;-trim.&lt;ext&gt;</c>", with a counter
    /// appended when that's taken — trimming the same recording twice is normal (two different
    /// excerpts), and silently overwriting the first result would lose work.</summary>
    private static string BuildOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? Path.GetTempPath();
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);

        var candidate = Path.Combine(dir, $"{name}-trim{ext}");
        var n = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name}-trim-{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}{ext}");
            n++;
        }
        return candidate;
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "VideoTrimService: could not clean up {Path}", path); }
    }
}
