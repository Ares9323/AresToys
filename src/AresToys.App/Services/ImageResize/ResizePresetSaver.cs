using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;

namespace AresToys.App.Services.ImageResize;

/// <summary>
/// Saves the Custom resize dialog's values as a new workflow with one "Resize image files" step.
/// Created the same way as a duplicated workflow (new "custom-" id, not built in) and written
/// through the profile store. The store decorator raises ProfilesChanged, which re-syncs the
/// Explorer submenu, so this class does not touch the menu.
/// </summary>
public sealed class ResizePresetSaver
{
    public const int MaxNameLength = 60;

    private readonly IPipelineProfileStore _profiles;

    public ResizePresetSaver(IPipelineProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
    }

    /// <summary>Creates the preset and returns its workflow id and final display name (trimmed,
    /// with " (2)", " (3)" appended when the name is taken).</summary>
    public async Task<(string Id, string Name)> SaveAsync(string name, JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Name is required.", nameof(name));
        if (trimmed.Length > MaxNameLength) trimmed = trimmed[..MaxNameLength].TrimEnd();

        var existing = await _profiles.ListAsync(CancellationToken.None).ConfigureAwait(false);
        var taken = new HashSet<string>(existing.Select(p => p.DisplayName), StringComparer.OrdinalIgnoreCase);
        var finalName = trimmed;
        for (var n = 2; taken.Contains(finalName); n++)
            finalName = $"{trimmed} ({n})";

        var stepConfig = (JsonObject)config.DeepClone();
        stepConfig["preset_name"] = finalName;
        stepConfig["show_notification"] = true;
        // A saved preset never overwrites originals by accident.
        stepConfig["replace_original"] = false;

        var id = $"custom-{Guid.NewGuid():N}";
        var profile = new PipelineProfile(
            id,
            finalName,
            $"hotkey:{id}",
            new[] { new PipelineStep(DefaultPipelineProfiles.ResizeImageFilesTaskId, stepConfig, Id: "resize-files") },
            Hotkey: null,
            IsBuiltIn: false);
        await _profiles.UpsertAsync(profile, CancellationToken.None).ConfigureAwait(false);
        return (id, finalName);
    }
}
