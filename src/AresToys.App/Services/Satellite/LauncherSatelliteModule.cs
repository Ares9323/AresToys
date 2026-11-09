using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Launcher;
using AresToys.App.Services.Logging;
using AresToys.App.Views;
using AresToys.Pipeline.Profiles;

namespace AresToys.App.Services.Satellite;

/// <summary>The Launcher inside the satellite: owns the <see cref="LauncherWindow"/> and its
/// <see cref="LauncherStore"/>, so the window lives in the non elevated process (Explorer can drop files on it).
/// <list type="bullet">
/// <item><c>launcher.toggle</c> {dragMode} answers {shown}: opens the window when it is closed (in drag mode when
/// asked), hides it when it is open.</item>
/// <item>event <c>launcher.state-changed</c>: the cells were changed behind the window's back (a backup import in the
/// main app); the store is told and a window on screen reloads.</item>
/// </list>
/// Calls back into the main app (workflows, activating or minimising elevated windows) go through
/// <see cref="ChannelLauncherHost"/>. Handlers run on the thread pool, so each one hops to the dispatcher.</summary>
public sealed class LauncherSatelliteModule : ISatelliteModule
{
    public const string ToggleRequest = "launcher.toggle";
    public const string StateChangedEvent = "launcher.state-changed";

    private IServiceProvider? _services;
    private SatelliteChannel? _channel;
    private Dispatcher? _dispatcher;
    private LauncherStore? _store;
    private ILogger? _logger;
    private LauncherWindow? _window;
    private bool _opening;
    private bool _stopped;

    public string Name => SatelliteModuleNames.Launcher;

    public void Register(SatelliteChannel channel, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _channel = channel;
        _dispatcher = Application.Current.Dispatcher;
        _store = services.GetRequiredService<LauncherStore>();
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<LauncherSatelliteModule>();

        channel.Handle(ToggleRequest, OnToggleAsync);
        channel.EventReceived += OnEvent;
    }

    /// <summary>Creates the window, hidden, and loads its cells so the first open paints already populated (the pre-warm
    /// the main app used to do).</summary>
    public void Start()
    {
        _ = PrewarmAsync();
    }

    public void Stop()
    {
        _stopped = true;
        try
        {
            if (_channel is not null) _channel.EventReceived -= OnEvent;
            // App.IsShuttingDown is already set (OnExit runs before the satellite host is disposed), so Closing is
            // not cancelled and the window really closes.
            _window?.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Launcher module: closing the window failed");
        }
        _window = null;
    }

    private async Task PrewarmAsync()
    {
        try
        {
            var window = EnsureWindow();
            await window.PrepareAsync();
            StartupTrace.Mark("satellite: launcher ready");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Launcher module: the first load failed; the first open will load lazily");
        }
    }

    /// <summary>UI thread only. A request can arrive before <see cref="Start"/> ran, so the window is created on demand.</summary>
    private LauncherWindow EnsureWindow()
    {
        if (_window is not null) return _window;

        var services = _services ?? throw new InvalidOperationException("The launcher module is not registered.");
        var channel = _channel ?? throw new InvalidOperationException("The launcher module is not registered.");
        var loggers = services.GetRequiredService<ILoggerFactory>();
        var host = new ChannelLauncherHost(channel, CurrentMainPid(), loggers.CreateLogger<ChannelLauncherHost>());
        _window = new LauncherWindow(
            services.GetRequiredService<LauncherStore>(),
            services.GetRequiredService<IconService>(),
            host,
            services.GetRequiredService<IPipelineProfileStore>(),
            services.GetRequiredService<ILogger<LauncherWindow>>());
        return _window;
    }

    /// <summary>The main app's process id, from this process's own command line (the host keeps the parsed arguments
    /// to itself). 0 when absent: the activation request then skips the foreground permission step.</summary>
    private static int CurrentMainPid()
        => SatelliteArgs.TryParse(Environment.GetCommandLineArgs(), out var args) && args is not null ? args.MainPid : 0;

    private async Task<JsonElement?> OnToggleAsync(JsonElement? data)
    {
        var dragMode = data is { ValueKind: JsonValueKind.Object } d
                       && d.TryGetProperty("dragMode", out var flag)
                       && flag.ValueKind == JsonValueKind.True;
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The launcher module is not registered.");

        var shown = await dispatcher.InvokeAsync<Task<bool>>(() => ToggleAsync(dragMode)).Task.Unwrap();
        return SatelliteProtocol.ToElement(new { shown });
    }

    /// <summary>UI thread only. True when the window is on screen afterwards.</summary>
    private async Task<bool> ToggleAsync(bool dragMode)
    {
        if (_stopped) return false;

        var window = EnsureWindow();
        if (window.IsShown)
        {
            window.RequestHide();
            return false;
        }

        // A second press while the first is still loading the cells: that one is already opening it.
        if (_opening) return true;
        _opening = true;
        try
        {
            window.StartInDragMode = dragMode;
            await window.PrepareAsync();
            if (_stopped) return false;

            window.Show();
            window.Activate();
            // The hotkey went to the main app, so this process is not the foreground one: Activate alone may leave the
            // window without keyboard focus. The main app granted the right just before sending the request.
            TargetWindowTracker.ForceForeground(new WindowInteropHelper(window).Handle);
            return true;
        }
        finally
        {
            _opening = false;
        }
    }

    private void OnEvent(string name, JsonElement? data)
    {
        if (!string.Equals(name, StateChangedEvent, StringComparison.Ordinal)) return;

        _store?.BumpStateVersion();
        _ = _dispatcher?.InvokeAsync(async () =>
        {
            try
            {
                // Hidden: the bumped version makes the next open reload by itself.
                if (_window is { IsShown: true } window) await window.ReloadStateAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Launcher module: reloading the cells failed");
            }
        });
    }
}
