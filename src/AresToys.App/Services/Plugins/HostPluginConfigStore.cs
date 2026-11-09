using AresToys.PluginContracts;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Plugins;

/// <summary>Concrete <see cref="IPluginConfigStore"/> backed by the global
/// <see cref="ISettingsStore"/>. Namespaces every key under <c>plugin.{uploaderId}.{key}</c>
/// so two uploaders can store the same logical key (e.g. both an <c>"api_key"</c>) without
/// stepping on each other.</summary>
internal sealed class HostPluginConfigStore : IPluginConfigStore
{
    private readonly ISettingsStore _settings;
    private readonly string _prefix;

    public HostPluginConfigStore(ISettingsStore settings, string uploaderId)
    {
        _settings = settings;
        _prefix = $"plugin.{uploaderId}.";
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        => _settings.GetAsync(_prefix + key, cancellationToken);

    public Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
        => _settings.SetAsync(_prefix + key, value, sensitive, cancellationToken);

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        => _ = await _settings.RemoveAsync(_prefix + key, cancellationToken).ConfigureAwait(false);
}
