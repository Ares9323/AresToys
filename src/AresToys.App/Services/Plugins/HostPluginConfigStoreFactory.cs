using AresToys.PluginContracts;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Plugins;

/// <summary>Default factory: hands every uploader a <see cref="HostPluginConfigStore"/> tied to
/// its own id-prefixed key namespace inside the global <see cref="ISettingsStore"/>. Singleton-
/// safe: the underlying store is shared, the wrapper is cheap and stateless beyond its id.</summary>
public sealed class HostPluginConfigStoreFactory : IPluginConfigStoreFactory
{
    private readonly ISettingsStore _settings;

    public HostPluginConfigStoreFactory(ISettingsStore settings) => _settings = settings;

    public IPluginConfigStore Create(string uploaderId) => new HostPluginConfigStore(_settings, uploaderId);
}
