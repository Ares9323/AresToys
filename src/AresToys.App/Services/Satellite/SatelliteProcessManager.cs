using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Satellite;

/// <summary>Main app side of the satellite process: starts it, watches it, restarts it when it dies and stops it. The
/// satellite is this same exe run with <c>--satellite</c>; when the main app is elevated it is started with the token
/// of the shell, so it is not elevated and Explorer can drop files on its windows. Nothing here throws to the caller
/// and nothing blocks it except <see cref="Stop"/>, which waits at most a few seconds.</summary>
public sealed class SatelliteProcessManager : IDisposable
{
    /// <summary>Wait before restart 1, 2 and 3. A satellite that dies a fourth time inside <see cref="RestartWindow"/> is left dead.</summary>
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];
    private static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(60);

    private readonly SatelliteServer _server;
    private readonly ILogger<SatelliteProcessManager> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Queue<DateTime> _restarts = new();
    private string[] _modules = [];
    private Process? _process;
    private bool _startScheduled;
    private bool _stopped;

    public SatelliteProcessManager(SatelliteServer server, ILogger<SatelliteProcessManager> logger)
    {
        _server = server;
        _logger = logger;
    }

    /// <summary>Starts the satellite for <paramref name="modules"/> on the thread pool and returns at once. Does
    /// nothing when a satellite process is already alive or a start is already on its way.</summary>
    public void EnsureStarted(IReadOnlyCollection<string> modules)
    {
        lock (_gate)
        {
            _stopped = false;
            if (_startScheduled || IsAlive(_process)) return;
            _modules = [.. modules];
            _startScheduled = true;
        }

        _ = Task.Run(StartAsync);
    }

    /// <summary>Asks the satellite to quit, waits up to 2 s for it to exit and kills it if it is still alive. The
    /// satellite is not restarted afterwards.</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            _stopped = true;
            process = _process;
        }

        if (process is null) return;

        try
        {
            if (_server.Current is { IsOpen: true } channel)
                channel.RequestAsync("quit", null, 1000).GetAwaiter().GetResult();
            if (process.WaitForExit(2000)) return;
            _logger.LogWarning("Satellite did not quit in time, killing it");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(1000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _logger.LogWarning(ex, "Satellite already gone or cannot be stopped");
        }
    }

    private async Task StartAsync()
    {
        try
        {
            string[] modules;
            lock (_gate)
            {
                if (_stopped)
                {
                    _startScheduled = false;
                    return;
                }
                modules = _modules;
            }

            var process = Launch(modules);
            var stopNow = false;
            lock (_gate)
            {
                _startScheduled = false;
                if (_stopped) stopNow = true;
                else _process = process;
            }

            if (stopNow)
            {
                Kill(process);
                return;
            }

            if (process is null)
            {
                await RestartAfterBackoffAsync().ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("Satellite started, pid {Pid}, modules {Modules}", process.Id, string.Join(",", modules));
            process.EnableRaisingEvents = true;
            process.Exited += OnExited;
        }
        catch (Exception ex)
        {
            lock (_gate) _startScheduled = false;
            _logger.LogError(ex, "Satellite start failed");
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var process = (Process)sender!;
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            if (_stopped) return;
        }

        _logger.LogWarning("Satellite exited unexpectedly");
        _ = Task.Run(RestartAfterBackoffAsync);
    }

    private async Task RestartAfterBackoffAsync()
    {
        TimeSpan delay;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            while (_restarts.Count > 0 && now - _restarts.Peek() > RestartWindow) _restarts.Dequeue();
            if (_restarts.Count >= Backoff.Length)
            {
                _logger.LogWarning("Satellite died {Count} times in {Seconds} s, not restarting it", _restarts.Count + 1, RestartWindow.TotalSeconds);
                return;
            }

            delay = Backoff[_restarts.Count];
            _restarts.Enqueue(now);
            if (_startScheduled || _stopped) return;
            _startScheduled = true;
        }

        try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (_gate) _startScheduled = false;
            return;
        }

        await StartAsync().ConfigureAwait(false);
    }

    private Process? Launch(string[] modules)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The exe path is unknown.");
        var directory = Path.GetDirectoryName(exe);
        var arguments = $"{SatelliteProtocol.Arg} {SatelliteProtocol.ModulesPrefix}{string.Join(",", modules)} {SatelliteProtocol.MainPidPrefix}{Environment.ProcessId}";

        try
        {
            if (ElevationService.IsCurrentProcessElevated
                && ElevationService.TryStartWithShellToken(null, $"\"{exe}\" {arguments}", directory, -1, out var pid))
            {
                return Process.GetProcessById(pid);
            }
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Satellite could not start with the shell token, starting it as this process");
        }
        catch (ArgumentException)
        {
            return null; // started and gone before it could be opened
        }

        return Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = directory ?? string.Empty });
    }

    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private void Kill(Process? process)
    {
        if (process is null) return;
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _logger.LogWarning(ex, "Satellite already gone or cannot be killed");
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Cancel();
        _cts.Dispose();
    }
}
