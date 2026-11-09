using AresToys.PluginContracts;

namespace AresToys.Plugins;

/// <summary>
/// Host-side abstraction over the active uploader catalog. Implementation lives in <c>AresToys.App</c>
/// (<c>PluginRegistry</c>) and applies the user's enabled/disabled toggle before returning an
/// uploader, so disabled plugins are invisible to the pipeline.
/// </summary>
public interface IUploaderResolver
{
    Task<IUploader?> ResolveAsync(string uploaderId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the user's selected uploaders for a given category ("image", "file", "text",
    /// "video"). Filters by: plugin enabled, capability matches the category, present in the
    /// user's per-category selection list. Order follows the persisted list.
    /// </summary>
    Task<IReadOnlyList<IUploader>> ResolveCategoryAsync(UploaderCapabilities category, CancellationToken cancellationToken);

    /// <summary>Persisted user selection for a given category. Used by the settings UI.</summary>
    Task<IReadOnlyList<string>> GetSelectedIdsAsync(UploaderCapabilities category, CancellationToken cancellationToken);
    Task SetSelectedIdsAsync(UploaderCapabilities category, IReadOnlyList<string> ids, CancellationToken cancellationToken);
}
