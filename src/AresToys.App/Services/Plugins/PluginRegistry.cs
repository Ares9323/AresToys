using AresToys.PluginContracts;
using AresToys.Plugins;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Plugins;

/// <summary>
/// Catalog of every <see cref="IUploader"/> the host knows about — both built-in (DI) and ones
/// loaded at runtime from <c>.sxcu</c> files via <c>CustomUploaderRegistry</c>. Per-category
/// selection (Image / File / Text / Video) is persisted here so the upload pipeline knows which
/// destinations to fan out to. Per-uploader enable/disable lived here in the old DLL-plugin era;
/// it's gone now — uploaders are always available, the user just picks them per category.
/// </summary>
public sealed class PluginRegistry : IUploaderResolver
{
    private readonly ISettingsStore _settings;
    private readonly IReadOnlyDictionary<string, IUploader> _uploaders;

    public PluginRegistry(ISettingsStore settings, IEnumerable<IUploader> uploaders)
    {
        _settings = settings;
        _uploaders = uploaders.ToDictionary(u => u.Id, StringComparer.Ordinal);
    }

    /// <summary>Resolve an uploader by id. Returns null if it isn't registered.</summary>
    public Task<IUploader?> ResolveAsync(string uploaderId, CancellationToken cancellationToken)
        => Task.FromResult(_uploaders.TryGetValue(uploaderId, out var u) ? u : null);

    /// <summary>All registered uploaders (used by the Settings UI listing).</summary>
    public IReadOnlyList<IUploader> AllUploaders => _uploaders.Values.ToList();

    /// <summary>Look up an uploader by id without going through async resolve.</summary>
    public IUploader? GetUploader(string id) => _uploaders.TryGetValue(id, out var u) ? u : null;

    public async Task<IReadOnlyList<string>> GetSelectedIdsAsync(UploaderCapabilities category, CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(SelectionKey(category), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task SetSelectedIdsAsync(UploaderCapabilities category, IReadOnlyList<string> ids, CancellationToken cancellationToken)
        => _settings.SetAsync(SelectionKey(category), string.Join(',', ids), sensitive: false, cancellationToken);

    public async Task<IReadOnlyList<IUploader>> ResolveCategoryAsync(UploaderCapabilities category, CancellationToken cancellationToken)
    {
        var primary = await ResolveStrictCategoryAsync(category, cancellationToken).ConfigureAwait(false);
        if (primary.Count > 0) return primary;

        // Fallback: if the user hasn't configured a category-specific list, fall back to the
        // generic "file" selection. Generic file hosts (Catbox / S3 / …) cover images / video /
        // text fine; specific image hosts (Imgur, etc.) only show up when the user has explicitly
        // opted in, so this fallback never silently changes their preference.
        // Url is excluded from the fallback — a file host can't shorten a URL, and silently
        // dumping the URL as a text file would be a confusing surprise to the user.
        if (category != UploaderCapabilities.File && category != UploaderCapabilities.Url)
        {
            return await ResolveStrictCategoryAsync(UploaderCapabilities.File, cancellationToken).ConfigureAwait(false);
        }
        return [];
    }

    private async Task<IReadOnlyList<IUploader>> ResolveStrictCategoryAsync(UploaderCapabilities category, CancellationToken cancellationToken)
    {
        var selected = await GetSelectedIdsAsync(category, cancellationToken).ConfigureAwait(false);
        var result = new List<IUploader>(selected.Count);
        foreach (var id in selected)
        {
            if (!_uploaders.TryGetValue(id, out var uploader)) continue;
            if ((uploader.Capabilities & category) == 0) continue; // capability mismatch
            result.Add(uploader);
        }
        return result;
    }

    private static string SelectionKey(UploaderCapabilities category) => $"uploaders.{category.ToString().ToLowerInvariant()}";
}
