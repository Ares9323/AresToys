using System.IO;
using AresToys.Storage.Paths;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.NewPlus;

/// <summary>What the New+ Explorer menu shows and how a template is turned into a file. Written by
/// the New+ settings tab; <see cref="NewPlusContextMenuSync"/> and <see cref="NewPlusService"/> read
/// it. The defaults are the PowerToys ones, except that the module itself starts off (like the
/// other Windows tools) and the template folder lives in the AresToys data folder.</summary>
internal sealed record NewPlusSettings(
    bool Enabled,
    string TemplateFolder,
    bool HideFileExtension,
    bool HideStartingDigits,
    bool ReplaceVariables,
    bool HideBuiltInNew)
{
    public const string KeyEnabled = "newplus.enabled";
    public const string KeyTemplateFolder = "newplus.templates.folder";
    public const string KeyHideFileExtension = "newplus.hide_extension";
    public const string KeyHideStartingDigits = "newplus.hide_leading_digits";
    public const string KeyReplaceVariables = "newplus.replace_variables";
    public const string KeyHideBuiltInNew = "newplus.hide_builtin_new";

    /// <summary>The template folder when the user never picked one: next to the database, so a
    /// portable install carries its templates along.</summary>
    public static string DefaultTemplateFolder(IStoragePathResolver paths)
        => Path.Combine(paths.ResolveRoot(), "NewPlus", "Templates");

    public static async Task<NewPlusSettings> LoadAsync(ISettingsStore store, IStoragePathResolver paths, CancellationToken ct)
    {
        var enabled = await store.GetAsync(KeyEnabled, ct).ConfigureAwait(false);
        var folder = await store.GetAsync(KeyTemplateFolder, ct).ConfigureAwait(false);
        var hideExt = await store.GetAsync(KeyHideFileExtension, ct).ConfigureAwait(false);
        var hideDigits = await store.GetAsync(KeyHideStartingDigits, ct).ConfigureAwait(false);
        var replace = await store.GetAsync(KeyReplaceVariables, ct).ConfigureAwait(false);
        var hideNew = await store.GetAsync(KeyHideBuiltInNew, ct).ConfigureAwait(false);

        return new NewPlusSettings(
            Enabled: enabled == "true",
            TemplateFolder: string.IsNullOrWhiteSpace(folder) ? DefaultTemplateFolder(paths) : folder.Trim(),
            HideFileExtension: hideExt != "false",
            HideStartingDigits: hideDigits != "false",
            ReplaceVariables: replace == "true",
            HideBuiltInNew: hideNew == "true");
    }

    public async Task SaveAsync(ISettingsStore store, CancellationToken ct)
    {
        await store.SetAsync(KeyEnabled, Enabled ? "true" : "false", sensitive: false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyTemplateFolder, TemplateFolder, sensitive: false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyHideFileExtension, HideFileExtension ? "true" : "false", sensitive: false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyHideStartingDigits, HideStartingDigits ? "true" : "false", sensitive: false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyReplaceVariables, ReplaceVariables ? "true" : "false", sensitive: false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyHideBuiltInNew, HideBuiltInNew ? "true" : "false", sensitive: false, ct).ConfigureAwait(false);
    }
}
