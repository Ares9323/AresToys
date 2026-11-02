using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Satellite;

namespace AresToys.App.Services.Launcher;

/// <summary>Main app side of the Launcher, which lives in the satellite. Everything the main app does with the
/// Launcher goes through here: opening and closing it (<see cref="ToggleAsync"/>), telling it that its cells changed
/// behind its back (<see cref="NotifyStateChanged"/>), and answering what the satellite cannot do itself because it
/// is not elevated: running a workflow, activating an elevated window, minimising an elevated program.
/// <para>The answering handlers are registered when the satellite connects (<c>SatelliteServer.Connected</c> fires
/// before the channel reads, so no request can arrive first). They run on the thread pool.</para>
/// <para>Singleton in the main container. Create it before <see cref="SatelliteService.Apply"/>.</para></summary>
public sealed class LauncherHostBridge : IDisposable
{
    /// <summary>How long the satellite gets to answer a toggle before the bridge gives up on it.</summary>
    public const int ToggleTimeoutMs = 1500;

    private readonly SatelliteService _satellite;
    private readonly IServiceProvider _services;
    private readonly ILogger<LauncherHostBridge> _logger;
    private int _disposed;

    /// <param name="services">Resolves <see cref="WorkflowRunner"/> when a workflow cell fires, instead of taking it
    /// as a constructor dependency: the pipeline executor graph behind it can reach the tasks that use this bridge,
    /// so asking for it up front would be a dependency cycle for the container.</param>
    public LauncherHostBridge(SatelliteService satellite, IServiceProvider services, ILogger<LauncherHostBridge> logger)
    {
        _satellite = satellite;
        _services = services;
        _logger = logger;
        _satellite.Server.Connected += OnConnected;
        // A satellite that connected before this bridge existed would have no handlers.
        if (_satellite.Server.Current is { } existing) OnConnected(existing);
    }

    /// <summary>Opens the Launcher in the satellite, or closes it when it is open. True when it is on screen
    /// afterwards. When there is no satellite to ask (not running, not answering, module off) it logs, asks
    /// <see cref="SatelliteService.Apply"/> to bring the satellite back once and returns false. Never blocks the
    /// caller and never throws.</summary>
    public async Task<bool> ToggleAsync(bool dragMode)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            var channel = _satellite.Server.Current;
            SatelliteMessage? answer = null;
            if (channel is not null)
            {
                // The hotkey arrived here, so this process owns the foreground right. Give it away to the satellite,
                // which then puts its window in front (TargetWindowTracker.ForceForeground).
                AllowAnyProcessForeground();
                answer = await channel.RequestAsync(LauncherSatelliteModule.ToggleRequest,
                    SatelliteProtocol.ToElement(new { dragMode }), ToggleTimeoutMs).ConfigureAwait(false);
            }

            if (answer is { Ok: true })
            {
                return answer.Data is { ValueKind: JsonValueKind.Object } data
                       && data.TryGetProperty("shown", out var shown)
                       && shown.ValueKind == JsonValueKind.True;
            }

            _logger.LogWarning("Launcher: the satellite did not open it ({Reason}), restarting it if needed",
                channel is null ? "no satellite connected" : answer is null ? "no answer" : answer.Error ?? "refused");
            _satellite.Apply();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Launcher: toggling it in the satellite failed");
            return false;
        }
    }

    /// <summary>Tells the Launcher that the cells in the settings store changed behind its back (a backup import), so
    /// it reloads on the next open, or at once when it is on screen. No-op without a satellite.</summary>
    public void NotifyStateChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _satellite.Server.Current?.Publish(LauncherSatelliteModule.StateChangedEvent);
    }

    private void OnConnected(SatelliteChannel channel)
    {
        channel.Handle(ChannelLauncherHost.WorkflowRunRequest, OnWorkflowRun);
        channel.Handle(ChannelLauncherHost.ActivateRequest, OnActivateAsync);
        channel.Handle(ChannelLauncherHost.MinimizeRequest, OnMinimize);
    }

    private Task<JsonElement?> OnWorkflowRun(JsonElement? data)
    {
        var id = ReadString(data, "id");
        if (!string.IsNullOrWhiteSpace(id))
            _services.GetRequiredService<WorkflowRunner>().RunDetached(id);
        return Task.FromResult<JsonElement?>(null);
    }

    private Task<JsonElement?> OnActivateAsync(JsonElement? data)
    {
        var title = ReadString(data, "title");
        var process = ReadString(data, "process");
        return Task.Run<JsonElement?>(() =>
        {
            var activated = WindowActivator.TryActivate(title, process);
            return SatelliteProtocol.ToElement(new { activated });
        });
    }

    private Task<JsonElement?> OnMinimize(JsonElement? data)
    {
        var path = ReadString(data, "path");
        if (!string.IsNullOrEmpty(path))
            WindowMinimizer.MinimizeWhenReady(null, path, _logger);
        return Task.FromResult<JsonElement?>(null);
    }

    private static string? ReadString(JsonElement? data, string name)
        => data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void AllowAnyProcessForeground()
    {
        try { AllowSetForegroundWindow(AsfwAny); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "Launcher: AllowSetForegroundWindow unavailable");
        }
    }

    private const uint AsfwAny = unchecked((uint)-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _satellite.Server.Connected -= OnConnected;
    }
}
