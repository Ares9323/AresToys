using Microsoft.Extensions.Logging;
using AresToys.App.Services.Satellite;

namespace AresToys.App.Services.Launcher;

/// <summary>Tells the satellite when the theme or the UI language changed in the main app, so its windows (Launcher,
/// later Wormholes) repaint with the new colours and strings. Module independent: it knows nothing about what the
/// satellite hosts. The satellite reloads both from the settings store, so the setting must already be saved when the
/// event goes out.
/// <para>Singleton in the main container, created at start-up before the satellite is started. Disposing it
/// unsubscribes.</para></summary>
public sealed class AppStateBroadcaster : IDisposable
{
    /// <summary>The language is saved by a fire and forget write right before <c>CultureChanged</c> is raised, so the
    /// event is held back this long to let the write land before the satellite reads the store.</summary>
    public const int CultureSettleMs = 250;

    private readonly SatelliteService _satellite;
    private readonly ThemeService _theme;
    private readonly LocalizationService _localization;
    private readonly ILogger<AppStateBroadcaster> _logger;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public AppStateBroadcaster(SatelliteService satellite, ThemeService theme, LocalizationService localization,
        ILogger<AppStateBroadcaster> logger)
    {
        _satellite = satellite;
        _theme = theme;
        _localization = localization;
        _logger = logger;
        _theme.Changed += OnThemeChanged;
        _localization.CultureChanged += OnCultureChanged;
    }

    // ThemeService.SetAsync saves every colour before it raises Changed, so the store is current here.
    private void OnThemeChanged(object? sender, EventArgs e) => Publish(SatelliteHost.ThemeChangedEvent);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        // Nobody to tell: skip the wait. The satellite reads the language when it starts.
        if (_satellite.Server.Current is null) return;
        _ = PublishAfterSettleAsync();
    }

    private async Task PublishAfterSettleAsync()
    {
        try
        {
            await Task.Delay(CultureSettleMs, _cts.Token).ConfigureAwait(false);
            Publish(SatelliteHost.CultureChangedEvent);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void Publish(string name)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var channel = _satellite.Server.Current;
        if (channel is null) return;
        if (!channel.Publish(name))
            _logger.LogDebug("Satellite event '{Event}' not sent: the channel is closed", name);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _theme.Changed -= OnThemeChanged;
        _localization.CultureChanged -= OnCultureChanged;
        _cts.Cancel();
    }
}
