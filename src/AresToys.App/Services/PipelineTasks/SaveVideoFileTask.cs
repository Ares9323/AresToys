using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using AresToys.App.Services.Recording;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Tasks;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Saves a video produced by <see cref="RecordScreenTask"/> to the configured
/// capture folder, optionally transcoding into a different container/codec. Mirrors
/// <see cref="SaveToFileTask"/> for raster images but routes through ffmpeg when the user's
/// target format doesn't match the recorded MP4.
/// <para>
/// Inputs (bag): <c>local_path</c> (the recorder's temp MP4, moved to the destination or used
/// as ffmpeg's input file), <c>file_extension</c>. <c>payload_bytes</c> is only a fallback for
/// producers that hand over bytes instead of a file: recordings are never loaded into memory.
/// </para>
/// <para>
/// Outputs (bag): <c>local_path</c> (final saved path), <c>text</c> (= local_path), updated
/// <c>file_extension</c> if a transcode happened, updated <c>new_item</c> (BlobRef + path
/// payload) so AddToHistoryTask points the history row at the final file.
/// </para>
/// <para>
/// Config: <c>format</c> (mp4 / gif / webm / mov, default mp4), <c>folder</c>,
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

        // Primary input is the file at bag.local_path (RecordScreenTask's temp recording): the
        // video is never loaded into memory (issue #28, a 250MB gif ended up copied dozens of
        // times). bag.payload_bytes is only a fallback for producers that hand over bytes.
        var sourcePath = context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawLocal) && rawLocal is string lp && File.Exists(lp)
            ? lp
            : null;
        var bytes = context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var rawBytes) && rawBytes is byte[] b ? b : null;
        if (sourcePath is null && bytes is null)
        {
            _logger.LogWarning("SaveVideoFileTask: neither bag.local_path nor bag.payload_bytes available, skipping (expected after RecordScreenTask).");
            return;
        }
        // Only the recorder's own temp file may be moved or deleted: any other local_path
        // belongs to the user (or to an earlier step) and is copied instead.
        var ownsSource = sourcePath is not null && IsPipelineTempRecording(sourcePath);

        var sourceExt = context.Bag.TryGetValue(PipelineBagKeys.FileExtension, out var rawExt) && rawExt is string ext
            ? ext.TrimStart('.').ToLowerInvariant()
            : "mp4";

        var targetExtRaw = ((string?)config?["format"])?.Trim().TrimStart('.').ToLowerInvariant();
        var targetExt = string.IsNullOrEmpty(targetExtRaw) || !SupportedFormats.Contains(targetExtRaw)
            ? sourceExt
            : targetExtRaw;

        var folder = await ResolveFolderAsync(config, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var baseName = await CaptureFileNamer.BuildAsync(_settings,
            context.Bag.TryGetValue(PipelineBagKeys.WindowTitle, out var rawTitle) ? rawTitle as string : null,
            context.Bag.TryGetValue(PipelineBagKeys.AppName, out var rawApp) ? rawApp as string : null,
            cancellationToken).ConfigureAwait(false);
        var fullPath = BuildDestinationPath(folder, baseName, targetExt);
        var transcoded = false;

        // Fast path: target format == source format (i.e. user picked mp4 and recorder gave us
        // mp4). Move / copy the file to the destination, no ffmpeg roundtrip. Same shape as
        // SaveToFileTask when the format override matches the bag's existing extension.
        if (string.Equals(targetExt, sourceExt, StringComparison.OrdinalIgnoreCase))
        {
            await PlaceSourceAsync(sourcePath, bytes, ownsSource, fullPath, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("SaveVideoFileTask: saved {Ext} to {Path}", targetExt, fullPath);
        }
        else
        {
            // Transcode path: ffmpeg reads the existing temp file. Passing a file is much
            // cheaper than piping through stdin (no double buffering, ffmpeg seeks freely for
            // the palette pass). Without a file (bytes-only producer) we materialise the payload
            // into a scratch file ourselves so the transcode still has a source.
            var scratch = sourcePath is null
                ? await MaterializeAsync(bytes!, sourceExt, cancellationToken).ConfigureAwait(false)
                : null;
            var transcodedOk = await TranscodeAsync(sourcePath ?? scratch!, fullPath, targetExt, cancellationToken)
                .ConfigureAwait(false);
            if (!transcodedOk)
            {
                _logger.LogWarning("SaveVideoFileTask: ffmpeg transcode {Src} → {Dst} failed; falling back to source-format write",
                    sourceExt, targetExt);
                // Fallback: keep the original recording with the source extension so the user
                // at least gets it. Re-derive the path with the source ext.
                TryDelete(fullPath);
                fullPath = BuildDestinationPath(folder, baseName, sourceExt);
                await PlaceSourceAsync(sourcePath, bytes, ownsSource, fullPath, cancellationToken).ConfigureAwait(false);
                targetExt = sourceExt;
            }
            else
            {
                transcoded = true;
                // The temp recording has been fully consumed by the transcode.
                if (ownsSource) TryDelete(sourcePath!);
                context.Bag[PipelineBagKeys.FileExtension] = targetExt;
                // Bytes handed over by the producer describe the old format: drop them so later
                // steps (Upload, Save as) read the transcoded file from bag.local_path instead.
                context.Bag.Remove(PipelineBagKeys.PayloadBytes);
            }
            if (scratch is not null) TryDelete(scratch);
        }

        context.Bag[PipelineBagKeys.LocalPath] = fullPath;
        context.Bag[PipelineBagKeys.Text] = fullPath;
        // Point the pending NewItem (built by RecordScreenTask, will be consumed by
        // AddToHistoryTask) at the final destination so the history row's BlobRef is the saved
        // file, not the temp recording. Video items carry only the path as payload.
        if (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem ni)
        {
            if (ni.Kind == ItemKind.Video)
            {
                ni = ni with
                {
                    Payload = Encoding.UTF8.GetBytes(fullPath),
                    PayloadSize = new FileInfo(fullPath).Length,
                    BlobRef = fullPath,
                };
            }
            else if (transcoded)
            {
                var newBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                ni = ni with { Payload = newBytes, PayloadSize = newBytes.LongLength, BlobRef = fullPath };
            }
            else
            {
                ni = ni with { BlobRef = fullPath };
            }
            context.Bag[PipelineBagKeys.NewItem] = ni;
        }

        if ((bool?)config?["showNotification"] == true && _notifier is not null)
        {
            _notifier.ShowFromBag(context, (string?)config?["notificationTitle"]);
        }
    }

    /// <summary>True when <paramref name="path"/> lives in the recorder's pipeline temp folder,
    /// i.e. a file this pipeline owns and may move or delete.</summary>
    internal static bool IsPipelineTempRecording(string path)
    {
        try
        {
            var root = Path.GetFullPath(RecordingCoordinator.PipelineTempFolder)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Put the source at <paramref name="destination"/>: move the owned temp recording,
    /// copy any other file, or write the bytes when there is no file at all.</summary>
    private async Task PlaceSourceAsync(string? sourcePath, byte[]? bytes, bool ownsSource, string destination, CancellationToken ct)
    {
        if (sourcePath is null)
        {
            await File.WriteAllBytesAsync(destination, bytes!, ct).ConfigureAwait(false);
            return;
        }
        if (ownsSource)
        {
            try
            {
                File.Move(sourcePath, destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "SaveVideoFileTask: moving {Src} failed, copying instead", sourcePath);
            }
            File.Copy(sourcePath, destination);
            TryDelete(sourcePath);
            return;
        }
        File.Copy(sourcePath, destination);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "SaveVideoFileTask: could not delete {Path}", path); }
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

    private static string BuildDestinationPath(string folder, string baseName, string ext)
    {
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

    /// <summary>Write bytes from a bytes-only producer into a scratch file ffmpeg can read.
    /// The caller deletes it once the transcode is done.</summary>
    private static async Task<string> MaterializeAsync(byte[] bytes, string sourceExt, CancellationToken ct)
    {
        var scratch = Path.Combine(RecordingCoordinator.PipelineTempFolder, $"transcode-input-{Guid.NewGuid():N}.{sourceExt}");
        Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
        await File.WriteAllBytesAsync(scratch, bytes, ct).ConfigureAwait(false);
        return scratch;
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
}
