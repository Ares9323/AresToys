namespace AresToys.PluginContracts;

/// <summary>Optional contract a built-in <see cref="IUploader"/> implements when it has user-tunable
/// settings (API key, default folder, public/private toggle, …). The host's Settings UI walks
/// <see cref="GetSettings"/> and renders one row per descriptor; values flow back via the store
/// returned by <see cref="IPluginConfigStoreFactory.Create"/>. Stateless uploaders (Catbox,
/// 0x0.st, paste.rs) skip this interface entirely.</summary>
public interface IConfigurableUploader
{
    IReadOnlyList<UploaderSetting> GetSettings();
}

/// <summary>Base type for a single user-facing setting row. Discriminated subclass per UI shape.
/// Records (immutable) keep the descriptor independent of the runtime value — values live in the
/// per-uploader <see cref="IPluginConfigStore"/>, keyed by <see cref="Key"/>.</summary>
public abstract record UploaderSetting(string Key, string Label, string? Description = null);

/// <summary>Plain string field — API key, account name, custom URL prefix, etc.
/// <see cref="Sensitive"/> = true encrypts the value via DPAPI in the settings store and masks the
/// UI input. <see cref="Default"/> is the initial value shown in the UI when no stored value
/// exists — uploaders should ALSO fall back to it at runtime when the user-stored value is
/// empty, so end-users get sensible behavior even if they wipe the field.</summary>
public sealed record StringSetting(
    string Key, string Label,
    string? Description = null,
    string? Placeholder = null,
    bool Sensitive = false,
    string? Default = null)
    : UploaderSetting(Key, Label, Description);

/// <summary>On / off toggle. Rendered as a checkbox / switch in the UI.</summary>
public sealed record BoolSetting(
    string Key, string Label,
    string? Description = null,
    bool Default = false)
    : UploaderSetting(Key, Label, Description);

/// <summary>Single-choice from a fixed list of options. Each option carries an internal
/// <see cref="DropdownOption.Value"/> stored as-is and a human <see cref="DropdownOption.Display"/>.
/// First option in the list is the default.</summary>
public sealed record DropdownSetting(
    string Key, string Label,
    IReadOnlyList<DropdownOption> Options,
    string? Description = null)
    : UploaderSetting(Key, Label, Description);

public sealed record DropdownOption(string Value, string Display);
