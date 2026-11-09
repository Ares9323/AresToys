using AresToys.Core.Pipeline;
using AresToys.Hotkeys;
using AresToys.Pipeline.Profiles;

namespace AresToys.App.Services.Hotkeys;

/// <summary>
/// Catalog of user-rebindable hotkeys. After the workflow refactor, the source of truth is the
/// <see cref="PipelineProfile.Hotkey"/> field on each profile; this service is a thin adapter that
/// lets the existing Settings → Hotkeys UI keep working until that tab is folded into the
/// Workflows view in the next sprint.
///
/// "Unbound" is represented as <see cref="HotkeyModifiers.None"/> + <c>VirtualKey == 0</c> so
/// callers don't have to deal with a nullable binding type. Use <see cref="ClearAsync"/> to remove
/// a binding the user no longer wants.
/// </summary>
public sealed class HotkeyConfigService
{
    public sealed record HotkeyEntry(string Id, string DisplayName, HotkeyModifiers DefaultModifiers, uint DefaultVirtualKey, bool IsBuiltIn);

    private readonly IPipelineProfileStore _profiles;

    public HotkeyConfigService(IPipelineProfileStore profiles)
    {
        _profiles = profiles;
    }

    public event EventHandler<HotkeyDefinition>? Changed;

    /// <summary>List of every workflow that can be bound to a hotkey, loaded from the profile
    /// store. Includes both built-in profiles and user-created custom workflows. Filters by
    /// <see cref="PipelineProfile.Trigger"/> starting with <c>"hotkey:"</c> so non-hotkey profiles
    /// (on-clipboard, manual-upload) don't pollute the rebind UI. Re-queried each time the
    /// hotkey settings tab is opened.</summary>
    public async Task<IReadOnlyList<HotkeyEntry>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var stored = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<HotkeyEntry>(stored.Count);
        foreach (var profile in stored)
        {
            if (!profile.Trigger.StartsWith("hotkey:", StringComparison.Ordinal)) continue;
            // For built-ins look up the seeded default so the Reset button has something to fall
            // back to even when the user has cleared the binding. Custom workflows have no
            // default — Reset on those just clears.
            var defaults = profile.IsBuiltIn
                ? DefaultPipelineProfiles.All.FirstOrDefault(p => p.Id == profile.Id)?.Hotkey
                : null;
            var mods = defaults is not null ? (HotkeyModifiers)defaults.Modifiers : HotkeyModifiers.None;
            var vk = defaults?.VirtualKey ?? 0u;
            list.Add(new HotkeyEntry(profile.Id, profile.DisplayName, mods, vk, profile.IsBuiltIn));
        }
        return list;
    }

    /// <summary>Returns the user's current binding for a workflow, or the unbound sentinel
    /// (<see cref="HotkeyModifiers.None"/>, VK 0) when the user has cleared it. The seeder ensures
    /// the profile exists by start-up; the fallback covers race conditions where the seed runs
    /// after a settings tab has already opened.</summary>
    public async Task<HotkeyDefinition> GetEffectiveAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (profile is not null)
        {
            if (profile.Hotkey is { } binding)
                return new HotkeyDefinition(id, (HotkeyModifiers)binding.Modifiers, binding.VirtualKey);
            return new HotkeyDefinition(id, HotkeyModifiers.None, 0);
        }
        // Profile not in store yet — fall back to seeded defaults if this is a known built-in id.
        var seeded = DefaultPipelineProfiles.All.FirstOrDefault(p => p.Id == id);
        if (seeded?.Hotkey is { } seededBinding)
            return new HotkeyDefinition(id, (HotkeyModifiers)seededBinding.Modifiers, seededBinding.VirtualKey);
        return new HotkeyDefinition(id, HotkeyModifiers.None, 0);
    }

    /// <summary>Find the workflow id that currently owns a (modifiers, virtualKey) combo, or null
    /// when the combo is free. Used by the rebind UI to warn the user before they steal a hotkey
    /// from another workflow. Excludes the workflow with id <paramref name="excludeId"/> so that
    /// "reassign to itself" (no-op) doesn't trip the duplicate check. Skips bindings with
    /// VK 0 (unbound) and ignores hotkey trigger profiles whose binding is null.</summary>
    public async Task<(string Id, string DisplayName)?> FindOwnerAsync(
        HotkeyModifiers modifiers, uint virtualKey, string? excludeId, CancellationToken cancellationToken)
    {
        if (virtualKey == 0) return null;
        var stored = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var profile in stored)
        {
            if (string.Equals(profile.Id, excludeId, StringComparison.Ordinal)) continue;
            if (profile.Hotkey is not { } binding) continue;
            if (binding.VirtualKey != virtualKey) continue;
            if ((HotkeyModifiers)binding.Modifiers != modifiers) continue;
            return (profile.Id, profile.DisplayName);
        }
        return null;
    }

    public async Task UpdateAsync(string id, HotkeyModifiers modifiers, uint virtualKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (profile is null) throw new ArgumentException($"workflow '{id}' not found", nameof(id));
        var newBinding = virtualKey == 0 ? null : new HotkeyBinding((int)modifiers, virtualKey);
        var updated = profile with { Hotkey = newBinding };
        await _profiles.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, new HotkeyDefinition(id, modifiers, virtualKey));
    }

    /// <summary>Removes the workflow's hotkey binding. The runtime hook unregisters; the workflow
    /// remains and can still be invoked from the tray / popup / future menu entries.</summary>
    public Task ClearAsync(string id, CancellationToken cancellationToken)
        => UpdateAsync(id, HotkeyModifiers.None, 0, cancellationToken);

    /// <summary>Tell subscribers (App.xaml.cs) that the workflow with the given id is going away
    /// so the keyboard hook unregisters its binding. Use when deleting a workflow — calling
    /// <see cref="ClearAsync"/> would fail because the profile is being removed entirely.</summary>
    public void NotifyHotkeyRemoved(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Changed?.Invoke(this, new HotkeyDefinition(id, HotkeyModifiers.None, 0));
    }

    /// <summary>Force-emit a Changed event with the given binding so the keyboard hook re-binds
    /// without us having to re-upsert the profile. Used by "Reset all to defaults" which has
    /// already overwritten the profile via the seeder.</summary>
    public void NotifyHotkeyRebound(string id, HotkeyModifiers modifiers, uint virtualKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Changed?.Invoke(this, new HotkeyDefinition(id, modifiers, virtualKey));
    }

    public async Task ResetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        // For built-ins reset to the seeded default; for custom workflows there's no default to
        // restore so reset is equivalent to clear.
        var seeded = DefaultPipelineProfiles.All.FirstOrDefault(p => p.Id == id);
        if (seeded?.Hotkey is { } binding)
            await UpdateAsync(id, (HotkeyModifiers)binding.Modifiers, binding.VirtualKey, cancellationToken).ConfigureAwait(false);
        else
            await ClearAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
