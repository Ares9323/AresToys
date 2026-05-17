using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using AresToys.App.Services.Recording;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Tasks;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Saves a video payload produced by <see cref="RecordScreenTask"/> to the configured
/// capture folder, optionally transcoding into a different container/codec. Mirrors
/// <see cref="SaveToFileTask"/> for raster images but routes through ffmpeg when the user's
/// target format doesn't match the recorded MP4.
/// <para>
/// Inputs (bag): <c>payload_bytes</c>, <c>file_extension</c>, <c>local_path</c> (the recorder's
/// temp MP4 — used as ffmpeg's input file so we don't roundtrip the bytes through stdin).
/// </para>
/// <para>
/// Outputs (bag): <c>local_path</c> (final saved path), <c>text</c> (= local_path), updated
/// <c>payload_bytes</c> + <c>file_extension</c> if a transcode happened, updated
/// <c>new_item</c> BlobRef so AddToHistoryTask points the history row at the final file.
/// </para>
/// <para>
/// Config: <c>format</c> (mp4 / gif / webm / mov — default mp4), <c>folder</c>,
/// <c>subfolder_pattern</c>, <c>showNotification</c>.
/// </para></summary>
public sealed class SaveVideoFileTask : IPipelineTask
{
    public const string TaskId = "arestoys.save-video-file";
    private const string DefaultFolder = "%USERPROFILE%\\Pictures\\AresToys";
    private const string FolderSettingKey = "capture.folder";
    private const string SubFolderPatternSettingKey = "capture.subfolder_pattern";

    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    { "mp4", "gif", "webm", "mov" };

    private readonly ISettingsStore _settings;
    private readonly FfmpegLocator _ffmpeg;
    private readonly IPipelineNotifier? _notifier;
    private readonly ILogger<SaveVideoFileTask> _logger;

    public SaveVideoFileTask(
        ISettingsStore settings,
        FfmpegLocator ffmpeg,
        ILogger<SaveVideoFileTask> logger,
        IPipelineNotifier? notifier = null)
    {
        _settings = settings;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _notifier = notifier;
    }

    public string Id => TaskId;
    public string DisplayName => "Save Video file";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var rawBytes) || rawBytes is not byte[] bytes)
        {
            _logger.LogWarning("SaveVideoFileTask: bag.payload_bytes missing or not byte[] — skipping (expected after RecordScreenTask).");
            return;
        }

        var sourceExt = context.Bag.TryGetValue(PipelineBagKeys.FileExtension, out var rawExt) && rawExt is string ext
            ? ext.TrimStart('.').ToLowerInvariant()
            : "mp4";

        var targetExtRaw = ((string?)config?["format"])?.Trim().TrimStart('.').ToLowerInvariant();
        var targetExt = string.IsNullOrEmpty(targetExtRaw) || !SupportedFormats.Contains(targetExtRaw)
            ? sourceExt
            : targetExtRaw;

        var folder = await ResolveFolderAsync(config, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var fullPath = BuildDestinationPath(folder, context, targetExt);

        // Fast path: target format == source format (i.e. user picked mp4 and recorder gave us
        // mp4). Write the bytes directly to the destination, no ffmpeg roundtrip. Same shape as
        // SaveToFileTask when the format override matches the bag's existing extension.
        if (string.Equals(targetExt, sourceExt, StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("SaveVideoFileTask: copied {Bytes} bytes ({Ext}) to {Path}", bytes.Length, targetExt, fullPath);
        }
        else
        {
            // Transcode path: ffmpeg reads the existing temp file (bag.local_path set by
            // RecordScreenTask) — passing a file is much cheaper than piping through stdin
            // (no double buffering, ffmpeg seeks freely for the palette pass). If local_path
            // is somehow missing or the file is gone, we materialise the payload_bytes into a
            // fresh temp file ourselves so the transcode still has a source.
            var sourcePath = await ResolveSourceFileAsync(context, bytes, sourceExt, cancellationToken)
                .ConfigureAwait(false);
            var transcodedOk = await TranscodeAsync(sourcePath, fullPath, targetExt, cancellationToken)
                .ConfigureAwait(false);
            if (!transcodedOk)
            {
                _logger.LogWarning("SaveVideoFileTask: ffmpeg transcode {Src} → {Dst} failed; falling back to source-format write",
                    sourceExt, targetExt);
                // Fallback: write the original bytes with the source extension so the user at
                // least gets the recording. Re-derive the path with the source ext.
                fullPath = BuildDestinationPath(folder, context, sourceExt);
                await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);
                targetExt = sourceExt;
            }
            else
            {
                // Re-read the transcoded bytes back into the bag so downstream Upload /
                // AddToHistory etc. see the transcoded format. The original mp4 payload is no
                // longer the truth once we've changed format.
                bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
                context.Bag[PipelineBagKeys.FileExtension] = targetExt;
            }
        }

        context.Bag[PipelineBagKeys.LocalPath] = fullPath;
        context.Bag[PipelineBagKeys.Text] = fullPath;
        // Point the pending NewItem (built by RecordScreenTask, will be consumed by
        // AddToHistoryTask) at the final destination so the history row's BlobRef is the saved
        // file, not the temp recording. The payload bytes stored in the item already match the
        // current bag.payload_bytes.
        if (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem ni)
        {
            context.Bag[PipelineBagKeys.NewItem] = ni with
            {
                Payload = bytes,
                PayloadSize = bytes.LongLength,
                BlobRef = fullPath,
            };
        }

        if ((bool?)config?["showNotification"] == true && _notifier is not null)
        {
            _notifier.ShowFromBag(context, (string?)config?["notificationTitle"]);
        }
    }

    private async Task<string> ResolveFolderAsync(JsonNode? config, CancellationToken ct)
    {
        var folderTemplate = (string?)config?["folder"]
            ?? await _settings.GetAsync(FolderSettingKey, ct).ConfigureAwait(false)
            ?? DefaultFolder;
        var folder = Environment.ExpandEnvironmentVariables(folderTemplate);
        var subPatternRaw = (string?)config?["subfolder_pattern"]
            ?? await _settings.GetAsync(SubFolderPatternSettingKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(subPatternRaw))
        {
            var sub = DatePatternExpander.Expand(Environment.ExpandEnvironmentVariables(subPatternRaw), DateTime.Now);
            folder = Path.Combine(folder, sub);
        }
        return folder;
    }

    private static string BuildDestinationPath(string folder, PipelineContext context, string ext)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var titleSlug = context.Bag.TryGetValue(PipelineBagKeys.WindowTitle, out var rawTitle) && rawTitle is string t
            ? SanitizeForFilename(t)
            : string.Empty;
        var baseName = string.IsNullOrEmpty(titleSlug) ? $"arestoys-rec-{stamp}" : $"arestoys-rec-{titleSlug}-{stamp}";
        var candidate = Path.Combine(folder, $"{baseName}.{ext}");
        // Collision guard, same shape as SaveToFileTask. Cheap; bounded.
        if (File.Exists(candidate))
        {
            for (var n = 1; n < 1000; n++)
            {
                var c = Path.Combine(folder, $"{baseName}-{n}.{ext}");
                if (!File.Exists(c)) { candidate = c; break; }
            }
        }
        return candidate;
    }

    private static async Task<string> ResolveSourceFileAsync(PipelineContext context, byte[] bytes, string sourceExt, CancellationToken ct)
    {
        if (context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var raw) && raw is string p && File.Exists(p))
            return p;
        // Bag.local_path lost / temp file evicted — rematerialise so ffmpeg has a file to read.
        var fallback = Path.Combine(Path.GetTempPath(), "AresToys", "recordings",
            $"transcode-input-{Guid.NewGuid():N}.{sourceExt}");
        Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
        await File.WriteAllBytesAsync(fallback, bytes, ct).ConfigureAwait(false);
        return fallback;
    }

    /// <summary>Launches ffmpeg with a target-format-specific encoder chain. Returns true on
    /// exit code 0 + non-empty output file. Standard recipes:
    /// <list type="bullet">
    /// <item><b>gif</b>: palette generation pass (split → palettegen → paletteuse) — same recipe
    /// the recorder uses for native gif capture, just driven from an existing video instead of
    /// gdigrab. Quality is reasonable for screen content; not optimal for photographic video.</item>
    /// <item><b>webm</b>: libvpx-vp9 with constant-quality (crf 30) — good size/quality balance,
    /// widely playable in browsers.</item>
    /// <item><b>mov</b>: re-mux as a QuickTime container, h264 source stream copied verbatim
    /// (-c:v copy). Lossless, instant — same bytes, different container.</item>
    /// <item><b>mp4</b>: should be handled by the fast path, but kept here as a safety net via
    /// stream-copy mux.</item>
    /// </list></summary>
    private async Task<bool> TranscodeAsync(string src, string dst, string targetExt, CancellationToken ct)
    {
        var ffmpeg = _ffmpeg.Find();
        if (ffmpeg is null)
        {
            _logger.LogWarning("SaveVideoFileTask: ffmpeg.exe not found — cannot transcode {Src} → {Dst}", src, dst);
            return false;
        }

        var args = targetExt switch
        {
            "gif" =>
                $"-y -i \"{src}\" -vf \"split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -loop 0 \"{dst}\"",
            "webm" =>
                $"-y -i \"{src}\" -c:v libvpx-vp9 -crf 30 -b:v 0 -row-mt 1 \"{dst}\"",
            "mov"  => $"-y -i \"{src}\" -c:v copy \"{dst}\"",
            _       => $"-y -i \"{src}\" -c:v copy \"{dst}\"",
        };

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
            if (p is null) return false;
            // Drain stderr so the pipe doesn't fill up on long encodes (ffmpeg writes progress
            // there). We don't care about the content unless the exit code is non-zero.
            _ = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                _logger.LogWarning("SaveVideoFileTask: ffmpeg exited {Code} for args: {Args}", p.ExitCode, args);
                return false;
            }
            return File.Exists(dst) && new FileInfo(dst).Length > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveVideoFileTask: ffmpeg launch failed");
            return false;
        }
    }

    private static string SanitizeForFilename(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '-' || c == ' ') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString().Trim('_');
        return s.Length > 40 ? s[..40] : s;
    }
}
