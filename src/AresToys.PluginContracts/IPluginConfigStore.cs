namespace AresToys.PluginContracts;

/// <summary>Per-uploader key/value store. Each built-in <see cref="IConfigurableUploader"/>
/// receives its own instance via <see cref="IPluginConfigStoreFactory.Create"/>; keys are
/// scoped to the uploader id by the host so two uploaders can use the same key name without
/// collision (e.g. both expose <c>"api_key"</c>). Values are strings — the uploader parses
/// into typed values when reading. <c>sensitive: true</c> on writes means the host should
/// encrypt at rest (DPAPI on Windows).</summary>
public interface IPluginConfigStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public interface IPluginConfigStoreFactory
{
    /// <summary>Create / retrieve the store for an uploader id (e.g. <c>"imgur"</c>).</summary>
    IPluginConfigStore Create(string uploaderId);
}
