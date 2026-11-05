using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Launcher;
using AresToys.App.Services.Logging;
using AresToys.Pipeline.Profiles;

namespace AresToys.App.Services.Satellite;

/// <summary>The satellite side of the bootstrap: what <c>AresToys.exe --satellite</c> does once its reduced container
/// is built. It connects to the main app over the satellite channel, answers <c>ping</c> and <c>quit</c>, hands the
/// channel to the <see cref="ISatelliteModule"/>s named in <c>--modules=</c> and leaves when the main app is gone, the
/// channel closes or the main app asks it to quit. If the main app cannot be reached the process exits at once, so
/// there is never an orphan.</summary>
public sealed class SatelliteHost : IDisposable
{
    /// <summary>How long the satellite looks for the main app's pipe before it gives up and exits.</summary>
    private const int ConnectTimeoutMs = 10000;

    /// <summary>The answer to <c>quit</c> goes through the channel's writer queue; this gives it time to leave before
    /// the process ends, otherwise the main app would wait out its request timeout.</summary>
    private const int QuitDelayMs = 150;

    private readonly SatelliteArgs _args;
    private readonly IServiceProvider _services;
    private readonly Application _app;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ISatelliteModule> _modules = [];
    private SatelliteChannel? _channel;
    private IDisposable? _watcher;
    private int _shuttingDown;
    private int _disposed;

    public SatelliteHost(SatelliteArgs args, IServiceProvider services, Application app)
    {
        _args = args;
        _services = services;
        _app = app;
        _logger = services.GetService<ILogger<SatelliteHost>>();
    }

    /// <summary>The extension point for the satellite modules: register each <see cref="ISatelliteModule"/> here
    /// (<c>services.AddSingleton&lt;ISatelliteModule, LauncherSatelliteModule&gt;()</c>) with the services it needs.
    /// The satellite container already has the settings store, <c>ThemeService</c>, <c>LocalizationService</c> and
    /// <c>IconService</c>; the module registrations are the only thing it adds on top.</summary>
    public static void AddModules(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The Launcher module and what its window needs besides the shared services. The module creates the window
        // and its channel host itself, because both need the connection that only exists once the satellite is connected.
        services.AddSingleton<LauncherStore>();
        services.AddSingleton<IPipelineProfileStore, SqlitePipelineProfileStore>();
        services.AddSingleton<ISatelliteModule, LauncherSatelliteModule>();

        // The Wormholes module: the real manager with its store, defaults, favicon pipeline and the colour wheel (the live
        // preview callback of the wheel cannot cross the pipe, so it stays here). Toasts are muted in this process: the
        // main app owns the toast activation.
        services.AddSingleton<AresToys.App.Services.Wormholes.IWormholeStore, AresToys.App.Services.Wormholes.WormholeStoreJson>();
        services.AddSingleton<AresToys.App.Services.Wormholes.DesktopLayerHost>();
        services.AddSingleton<AresToys.App.Services.Wormholes.WormholeDefaultsService>();
        services.AddSingleton<AresToys.App.Services.Wormholes.Favicons.FaviconCache>();
        services.AddSingleton<AresToys.App.Services.Wormholes.Favicons.IFaviconDownloader, AresToys.App.Services.Wormholes.Favicons.FaviconDownloader>();
        services.AddSingleton<AresToys.App.Services.Wormholes.Favicons.FaviconService>();
        services.AddSingleton<AresToys.Editor.Persistence.ColorRecentsStore>();
        services.AddSingleton<IToastNotifier, SatelliteToastNotifier>();
        services.AddSingleton<ScreenColorPickerService>();
        services.AddSingleton<ColorWheelLauncher>();
        services.AddSingleton<AresToys.App.Services.Wormholes.IWormholeWindowManager, AresToys.App.Services.Wormholes.WormholeWindowManager>();
        services.AddSingleton<ISatelliteModule, WormholesSatelliteModule>();

        // The Peek module: reads the selection of the active Explorer tab and shows the preview window. It needs
        // nothing beyond the shared services (theme, localization).
        services.AddSingleton<AresToys.App.Services.Peek.PeekOpenWithStore>();
        services.AddSingleton<AresToys.App.Services.Peek.PeekKeysStore>();
        services.AddSingleton<AresToys.App.Services.Peek.PeekWindowStore>();
        services.AddSingleton<AresToys.App.Services.Peek.PeekShowAsStore>();
        services.AddSingleton<ISatelliteModule, AresToys.App.Services.Peek.PeekSatelliteModule>();
    }

    /// <summary>Connects, registers the handlers and starts the modules. False when the satellite must exit: the main
    /// app could not be reached in time or a shutdown was already requested.</summary>
    public async Task<bool> RunAsync()
    {
        // Watching starts before the connect so a main app that dies while the satellite is still looking for its
        // pipe does not keep the satellite waiting for the full timeout.
        _watcher = SatelliteLifetime.WatchMainProcess(_args.MainPid, () => RequestShutdown(delayMs: 0));

        StartupTrace.Mark("satellite: connecting to the main app");
        var channel = await SatelliteClient.ConnectAsync(register: RegisterOnConnection, timeoutMs: RemainingConnectMs(), ct: _cts.Token);
        if (channel is null)
        {
            StartupTrace.Mark("satellite: main app not reachable, exiting");
            return false;
        }

        if (Volatile.Read(ref _shuttingDown) != 0) return false;
        StartupTrace.Mark("satellite: connected");

        foreach (var module in _modules)
        {
            try { module.Start(); }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Satellite module '{Name}' failed to start", module.Name);
                StartupTrace.Mark($"satellite: module {module.Name} failed to start: {ex.Message}");
            }
        }

        StartupTrace.Mark($"satellite: running, modules {string.Join(",", _modules.Select(m => m.Name))}");
        return true;
    }

    /// <summary>The connect budget counts from the start of the process, so building the container does not stretch
    /// the time an orphaned satellite lives.</summary>
    private static int RemainingConnectMs()
    {
        try
        {
            var age = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            return (int)Math.Clamp(ConnectTimeoutMs - age.TotalMilliseconds, 1000, ConnectTimeoutMs);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ConnectTimeoutMs;
        }
    }

    /// <summary>Runs on the channel before its read loop starts (on a pool thread), so the handlers exist when the
    /// first request from the main app arrives. The module registration itself is moved to the UI thread.</summary>
    private void RegisterOnConnection(SatelliteChannel channel)
    {
        try
        {
            _channel = channel;
            // Subscribed before the read loop starts, so a main app that drops right away is not missed.
            channel.Closed += () => RequestShutdown(delayMs: 0);
            channel.EventReceived += OnAppEvent;
            channel.Handle("ping", _ => Task.FromResult<JsonElement?>(null));
            channel.Handle("quit", _ =>
            {
                RequestShutdown(QuitDelayMs);
                return Task.FromResult<JsonElement?>(null);
            });

            _app.Dispatcher.Invoke(() => RegisterModules(channel));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Satellite could not register its handlers");
            StartupTrace.Mark("satellite: registering handlers failed: " + ex.Message);
        }
    }

    /// <summary>Event published by the main app when the user changed the theme (accent, colours): the satellite
    /// reloads it from the settings store, the same call the start-up uses.</summary>
    public const string ThemeChangedEvent = "app.theme-changed";

    /// <summary>Event published by the main app when the user changed the UI language.</summary>
    public const string CultureChangedEvent = "app.culture-changed";

    /// <summary>Module independent events: the theme and the language belong to the whole process, so every module
    /// (Launcher, Wormholes) gets fresh brushes and strings without handling them. Runs on the pool, reloads on the
    /// dispatcher. The main app saves the setting before it publishes.</summary>
    private void OnAppEvent(string name, JsonElement? data)
    {
        if (string.Equals(name, ThemeChangedEvent, StringComparison.Ordinal))
            _ = ReloadOnDispatcherAsync(() => _services.GetRequiredService<ThemeService>().LoadAsync(CancellationToken.None), "theme");
        else if (string.Equals(name, CultureChangedEvent, StringComparison.Ordinal))
            _ = ReloadOnDispatcherAsync(() => _services.GetRequiredService<LocalizationService>().LoadAsync(CancellationToken.None), "language");
    }

    private async Task ReloadOnDispatcherAsync(Func<Task> reload, string what)
    {
        try
        {
            if (Volatile.Read(ref _shuttingDown) != 0) return;
            await _app.Dispatcher.InvokeAsync(reload).Task.Unwrap();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Satellite could not reload the {What} after the main app changed it", what);
        }
    }

    private void RegisterModules(SatelliteChannel channel)
    {
        var available = _services.GetServices<ISatelliteModule>().ToList();
        foreach (var name in _args.Modules)
        {
            var module = available.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                _logger?.LogInformation("Satellite asked for module '{Name}', which this build does not have", name);
                continue;
            }

            try
            {
                module.Register(channel, _services);
                _modules.Add(module);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Satellite module '{Name}' failed to register", name);
                StartupTrace.Mark($"satellite: module {name} failed to register: {ex.Message}");
            }
        }
    }

    /// <summary>Ends the process once: stops a connect in progress and shuts the application down on the UI thread.</summary>
    private void RequestShutdown(int delayMs)
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;

        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* disposed: the process is already ending */ }

        _ = Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            _ = _app.Dispatcher.BeginInvoke(() => _app.Shutdown());
        });
    }

    /// <summary>Stops the modules (last registered first), the watcher and the channel. Called from
    /// <c>App.OnExit</c> on the UI thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _shuttingDown, 1);

        _cts.Cancel();
        _watcher?.Dispose();
        for (var i = _modules.Count - 1; i >= 0; i--)
        {
            try { _modules[i].Stop(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Satellite module '{Name}' failed to stop", _modules[i].Name); }
        }

        _channel?.Dispose();
    }
}
