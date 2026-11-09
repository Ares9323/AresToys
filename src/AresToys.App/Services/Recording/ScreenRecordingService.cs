using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using AresToys.Capture.Recording;

namespace AresToys.App.Services.Recording;

public sealed class ScreenRecordingService : IDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<ScreenRecordingService> _logger;
    private Process? _process;
    private string? _currentOutputPath;
    private readonly System.Collections.Generic.Queue<string> _recentStderr = new();
    private readonly object _stderrLock = new();

    public ScreenRecordingService(FfmpegLocator locator, ILogger<ScreenRecordingService> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool IsRecording => _process is { HasExited: false };
    public bool IsPaused { get; private set; }
    public string? CurrentOutputPath => _currentOutputPath;

    public event EventHandler? StateChanged;

    /// <summary>Spawn FFmpeg with gdigrab capturing the given region. Returns false if FFmpeg can't be
    /// found or another recording is already running.</summary>
    public bool TryStart(RecordingOptions options)
    {
        if (IsRecording)
        {
            _logger.LogInformation("TryStart: already recording, ignoring.");
            return false;
        }
        var ffmpeg = _locator.Find();
        if (ffmpeg is null)
        {
            _logger.LogWarning("TryStart: ffmpeg.exe not found.");
            return false;
        }

        var args = FfmpegArgsBuilder.Build(options);
        _logger.LogDebug("ffmpeg {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            // Match ShareX's setup byte-for-byte — getting any of these wrong (encoding, working dir,
            // pipe drain) ends in a 0-byte mp4 because ffmpeg's input/output loop blocks.
            WorkingDirectory = Path.GetDirectoryName(ffmpeg)!,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        try
        {
            _process = Process.Start(psi);
            if (_process is null) return false;
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            // CRITICAL: drain stderr/stdout in background. If we don't read these pipes, FFmpeg
            // eventually blocks on its own writes (Windows pipe buffer ≈ 4KB) and stops processing
            // input — including our "q" command — which leaves the mp4 unfinalized.
            // Log stderr at debug level so problems with ffmpeg are visible in logs.
            lock (_stderrLock) _recentStderr.Clear();
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                _logger.LogInformation("ffmpeg: {Line}", e.Data);
                lock (_stderrLock)
                {
                    _recentStderr.Enqueue(e.Data);
                    while (_recentStderr.Count > 40) _recentStderr.Dequeue();
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _currentOutputPath = options.OutputPath;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ffmpeg");
            _process?.Dispose();
            _process = null;
            return false;
        }
    }

    /// <summary>Send "q\n" on stdin so FFmpeg flushes and finalizes the output cleanly. Async so it
    /// doesn't freeze the UI. Generous timeout: long recordings can need a while to finalize.</summary>
    public async Task StopAsync()
    {
        var p = _process;
        if (p is null || p.HasExited) return;
        if (IsPaused) Resume(); // unfreeze first or "q" never reaches the input loop

        try
        {
            await p.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await p.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to write 'q' to ffmpeg stdin"); }

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* timeout */ }
        }

        if (!p.HasExited)
        {
            _logger.LogWarning("ffmpeg still running after 30s; sending second 'q'");
            try { await p.StandardInput.WriteLineAsync("q").ConfigureAwait(false); } catch { }
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await p.WaitForExitAsync(cts2.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (!p.HasExited)
        {
            _logger.LogError("ffmpeg unresponsive — killing. Output at {Path} may be truncated.", _currentOutputPath);
            try { p.Kill(); } catch { }
            using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await p.WaitForExitAsync(cts3.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        else
        {
            _logger.LogInformation("ffmpeg exited cleanly with code {Code}", p.ExitCode);
        }
    }

    /// <summary>Synchronous fire-and-forget stop for shutdown paths (Dispose). Doesn't await.</summary>
    public void Stop() => _ = StopAsync();

    /// <summary>Freeze the FFmpeg process via NtSuspendProcess. Frames stop being captured/encoded
    /// but the output container remains open, ready to resume.</summary>
    public void Pause()
    {
        var p = _process;
        if (p is null || p.HasExited || IsPaused) return;
        var rc = NtSuspendProcess(p.Handle);
        if (rc == 0) { IsPaused = true; StateChanged?.Invoke(this, EventArgs.Empty); }
        else _logger.LogWarning("NtSuspendProcess returned {Rc}", rc);
    }

    public void Resume()
    {
        var p = _process;
        if (p is null || p.HasExited || !IsPaused) return;
        var rc = NtResumeProcess(p.Handle);
        if (rc == 0) { IsPaused = false; StateChanged?.Invoke(this, EventArgs.Empty); }
        else _logger.LogWarning("NtResumeProcess returned {Rc}", rc);
    }

    /// <summary>Kill FFmpeg without finalizing and delete the partial output file.</summary>
    public void Abort()
    {
        var p = _process;
        if (p is null) return;
        if (IsPaused) Resume();
        try { if (!p.HasExited) p.Kill(); } catch { /* already dead */ }
        var path = _currentOutputPath;
        _currentOutputPath = null;
        if (!string.IsNullOrEmpty(path))
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete aborted recording {Path}", path); }
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = _process?.ExitCode ?? 0;
        if (code != 0)
        {
            string[] tail;
            lock (_stderrLock) tail = [.. _recentStderr];
            _logger.LogWarning("ffmpeg exited with non-zero code {Code} ({Hex}). Last stderr lines:\n{Tail}",
                code, "0x" + ((uint)code).ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                string.Join("\n", tail));
        }
        else
        {
            _logger.LogInformation("ffmpeg exited cleanly. Output: {Path}", _currentOutputPath);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _process?.Dispose();
        _process = null;
    }
}
