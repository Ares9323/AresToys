using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Recording;

/// <summary>
/// Generates a still-frame thumbnail for a recorded video by invoking the bundled ffmpeg with
/// <c>-ss 0.5 -frames:v 1</c>. Used by the clipboard popup's preview pane so the user can tell
/// recordings apart at a glance instead of just reading the filename. Results are memoized in a
/// process-local dictionary keyed by item id — generation costs ~200ms on a typical mp4 and the
/// user is likely to flip through multiple entries quickly, so caching matters. Cache is wiped
/// on app restart (no on-disk persistence yet — could be promoted to the item's Thumbnail
/// column later if the cost shows up in real usage).
/// </summary>
public sealed class VideoThumbnailService
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<VideoThumbnailService> _logger;
    private readonly ConcurrentDictionary<long, byte[]> _cache = new();

    public VideoThumbnailService(FfmpegLocator locator, ILogger<VideoThumbnailService> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <summary>Return a JPEG-encoded thumbnail (≤ 480px wide) for the given video file. Returns
    /// null when ffmpeg isn't available (user never installed it), when the file is missing, or
    /// when ffmpeg fails. Cached so repeated calls for the same <paramref name="itemId"/> don't
    /// re-spawn ffmpeg.</summary>
    public async Task<byte[]?> GenerateAsync(long itemId, string videoPath, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(itemId, out var cached)) return cached;

        var ffmpeg = _locator.Find();
        if (ffmpeg is null) return null;
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            // Fast seek (-ss before -i), single frame, max width 480 keeping aspect, JPEG on stdout.
            // -hide_banner + -loglevel error silence ffmpeg's normal noise so the captured stderr
            // only carries actual failures we want to surface in the log.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Ordering choice: `-ss` AFTER `-i` (slow / accurate seek) instead of before. Fast seek
        // (-ss before -i) is faster but assumes the container exposes an index — fails silently
        // on indexless / streaming-style containers like .ts (MPEG-TS), .flv, raw .h264, where
        // ffmpeg can't jump and instead returns an empty output. Slow seek decodes from start up
        // to the target time; we only ask for 0.5s in, so it's a handful of frames either way.
        //
        // `-map 0:v:0?` picks the first video stream explicitly: a few .ts captures have audio
        // before video in the program map and ffmpeg's default mapping would otherwise pull the
        // audio stream and fail to encode it as JPEG. The trailing `?` makes the map optional —
        // audio-only files return cleanly with no output instead of a hard error.
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add("00:00:00.5");
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0?");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale='min(480,iw)':-2");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("image2pipe");
        psi.ArgumentList.Add("-vcodec"); psi.ArgumentList.Add("mjpeg");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            using var ms = new MemoryStream();
            // Pipe stdout into memory. ffmpeg may hold stderr long enough to deadlock if we don't
            // also drain it; kick off a fire-and-forget read so the buffer never fills.
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("VideoThumbnailService: ffmpeg exit {Code} for {Path}. stderr={Err}", proc.ExitCode, videoPath, stderr.Trim());
                return null;
            }
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
            {
                _logger.LogWarning("VideoThumbnailService: ffmpeg produced empty output for {Path} (file shorter than 0.5s?). stderr={Err}", videoPath, stderr.Trim());
                return null;
            }
            _cache.TryAdd(itemId, bytes);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VideoThumbnailService: ffmpeg invocation failed for {Path}", videoPath);
            return null;
        }
    }

    /// <summary>Drop the cached thumbnail for the given item. Called when the item is deleted /
    /// its BlobRef changes so a future regenerate doesn't return stale bytes.</summary>
    public void Invalidate(long itemId) => _cache.TryRemove(itemId, out _);
}
