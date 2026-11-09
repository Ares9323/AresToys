using Microsoft.Extensions.Options;
using AresToys.Clipboard;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

public sealed class IncognitoModeService
{
    private const string SettingsKey = "clipboard.incognito.active";
    private readonly ISettingsStore _settings;
    private readonly IOptionsMonitor<CaptureGateOptions> _gateOptions;
    private bool _active;

    public IncognitoModeService(ISettingsStore settings, IOptionsMonitor<CaptureGateOptions> gateOptions)
    {
        _settings = settings;
        _gateOptions = gateOptions;
    }

    public bool IsActive => _active;

    public event EventHandler? StateChanged;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
        _active = stored == "1";
        ApplyToOptions();
    }

    public async Task SetAsync(bool active, CancellationToken cancellationToken)
    {
        if (_active == active) return;
        _active = active;
        await _settings.SetAsync(SettingsKey, active ? "1" : "0", sensitive: false, cancellationToken).ConfigureAwait(false);
        ApplyToOptions();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task ToggleAsync(CancellationToken cancellationToken) => SetAsync(!_active, cancellationToken);

    private void ApplyToOptions()
    {
        _gateOptions.CurrentValue.IncognitoActive = _active;
    }
}
