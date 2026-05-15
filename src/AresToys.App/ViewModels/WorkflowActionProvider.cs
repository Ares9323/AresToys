using AresToys.App.Services.Plugins;

namespace AresToys.App.ViewModels;

/// <summary>
/// Builds the runtime list of <see cref="WorkflowActionDescriptor"/>s the user can pick from in
/// the workflow editor. Combines the static catalog (built-in tasks like Capture / Save / Notify)
/// with one synthetic <c>arestoys.upload</c> descriptor per <em>enabled</em> uploader, so the picker
/// shows entries like "Upload to OneDrive" / "Upload to Catbox" alongside the generic
/// "Upload to selected image uploaders" / "...selected file uploaders" category entries.
///
/// Why dynamic: the set of available uploaders (built-in + external plugins, each toggleable in
/// Settings → Plugins) is only known at runtime. A static enum can't enumerate user-installed
/// plugins, and we don't want disabled uploaders cluttering the picker.
/// </summary>
public sealed class WorkflowActionProvider
{
    private readonly PluginRegistry _registry;

    public WorkflowActionProvider(PluginRegistry registry)
    {
        _registry = registry;
    }

    public Task<IReadOnlyList<WorkflowActionDescriptor>> GetAllAsync(CancellationToken cancellationToken)
    {
        var list = new List<WorkflowActionDescriptor>(WorkflowActionCatalog.All);
        foreach (var uploader in _registry.AllUploaders)
        {
            // Embed the uploader id verbatim — slug + hash format so JSON-string escaping isn't needed.
            var configJson = $"{{\"uploader\":\"{uploader.Id}\"}}";
            list.Add(new WorkflowActionDescriptor(
                TaskId: "arestoys.upload",
                DisplayName: $"Upload to {uploader.DisplayName}",
                Description: $"Upload the current bytes via the {uploader.DisplayName} uploader. Other uploaders aren't run.",
                Category: "Upload",
                DefaultConfigJson: configJson,
                // LocalizationKey unique-per-uploader so the localizer's resx lookup misses
                // (we don't ship per-uploader translations) and falls back to DisplayName above.
                // Without this every variant would share the catch-all WorkflowAction_arestoys_upload
                // key (= "Upload") and the picker showed N copies of just "Upload".
                LocalizationKey: "arestoys_upload_to_" + uploader.Id.Replace('.', '_').Replace('-', '_')));
        }
        return Task.FromResult<IReadOnlyList<WorkflowActionDescriptor>>(list);
    }
}
