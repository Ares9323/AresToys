using Microsoft.Extensions.Logging;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Satellite;

/// <summary>Main app side of the satellite. It owns the pipe server and the process manager, and keeps the satellite in
/// line with the settings: running while at least one of the Wormholes and Launcher modules is enabled, stopped when
/// none is. A satellite that runs for another module set than the settings now ask for is restarted by the next
/// <see cref="Apply"/>, so switching the Launcher or Wormholes module on or off needs no app restart.
/// The modules that move into the satellite reach their channel through <see cref="Server"/>
/// (<c>Server.Current</c>, <c>Server.Connected</c>).
/// <para>Singleton in the main container. Disposing it stops the satellite.</para></summary>
public sealed class SatelliteService : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<SatelliteService> _logger;
    private readonly SatelliteProcessManager _manager;
    // Not disposed: a late Apply must still be able to release it. Plain semaphores hold no handle until asked.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;
    // The module set the running satellite was started for; null while none is running. Touched under _gate only.
    private string? _runningModules;

    public SatelliteService(ISettingsStore settings, ILoggerFactory loggers)
    {
        _settings = settings;
        _logger = loggers.CreateLogger<SatelliteService>();
        Server = new SatelliteServer(loggers.CreateLogger<SatelliteServer>());
        _manager = new SatelliteProcessManager(Server, loggers.CreateLogger<SatelliteProcessManager>());
    }

    /// <summary>The pipe server. It starts listening with the first <see cref="Apply"/> that finds a module enabled.</summary>
    public SatelliteServer Server { get; }

    /// <summary>The process id of the running satellite, or 0 when none runs. Cheap: safe on the hook thread.</summary>
    public int SatelliteProcessId => _manager.ProcessId;

    /// <summary>Raised on a pool thread at the end of every <see cref="Apply"/> pass with the module names that are
    /// enabled now (empty when none). Main side pieces that only make sense while a satellite module runs, like the
    /// Peek hotkey, follow the switch through this instead of reading the settings a second time. Handlers must not throw.</summary>
    public event Action<IReadOnlyList<string>>? ModulesApplied;

    /// <summary>Reads which satellite modules are enabled and starts or stops the satellite to match. Returns at once
    /// (the work runs on the thread pool), is safe to call from any thread and as often as a setting changes: calls
    /// are serialised and starting an already running satellite with the same module set does nothing. When the set
    /// changed, the running satellite is stopped and started again for the new set.</summary>
    public void Apply()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = Task.Run(ApplyAsync);
    }

    private async Task ApplyAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return;

                var modules = await ReadEnabledModulesAsync().ConfigureAwait(false);
                var wanted = string.Join(",", modules);
                if (modules.Count > 0)
                {
                    Server.Start();
                    if (_runningModules is not null && _runningModules != wanted)
                    {
                        _logger.LogInformation("Satellite modules changed from {Old} to {New}, restarting it", _runningModules, wanted);
                        _manager.Stop();
                    }
                    _manager.EnsureStarted(modules);
                    _runningModules = wanted;
                }
                else
                {
                    _manager.Stop();
                    _runningModules = null;
                }

                ModulesApplied?.Invoke(modules);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the satellite settings failed");
        }
    }

    /// <summary>The same keys and defaults the main app uses for its module switches (<see cref="ModuleSettings"/>):
    /// the Launcher is on unless saved as false, Wormholes and Peek are off unless saved as true.</summary>
    private async Task<List<string>> ReadEnabledModulesAsync()
    {
        var launcherRaw = await _settings.GetAsync(ModuleSettings.LauncherKey, CancellationToken.None).ConfigureAwait(false);
        var wormholesRaw = await _settings.GetAsync(ModuleSettings.WormholesKey, CancellationToken.None).ConfigureAwait(false);
        var peekRaw = await _settings.GetAsync(ModuleSettings.PeekKey, CancellationToken.None).ConfigureAwait(false);

        var modules = new List<string>();
        if (string.Equals(wormholesRaw, "true", StringComparison.OrdinalIgnoreCase))
            modules.Add(SatelliteModuleNames.Wormholes);
        if (!string.Equals(launcherRaw, "false", StringComparison.OrdinalIgnoreCase))
            modules.Add(SatelliteModuleNames.Launcher);
        if (string.Equals(peekRaw, "true", StringComparison.OrdinalIgnoreCase))
            modules.Add(SatelliteModuleNames.Peek);
        return modules;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _manager.Dispose();
        Server.Dispose();
    }
}
