using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Hotkeys;
using AresToys.App.Views;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace AresToys.App.ViewModels;

/// <summary>
/// Backs the Settings → Workflows tab. Lists every pipeline profile (built-in + custom) loaded
/// from <see cref="IPipelineProfileStore"/>; the editor below mutates the currently-selected one
/// via <see cref="WorkflowEditorViewModel"/>. Owns the CRUD toolbar (add / rename / duplicate /
/// remove / reset-all) — the data layer just persists; the routing back to the keyboard hook
/// happens through <see cref="HotkeyConfigService"/>.
/// </summary>
public sealed partial class WorkflowsViewModel : ObservableObject
{
    /// <summary>Friendlier descriptions for the built-in profiles where the bare <c>Trigger</c>
    /// string isn't very readable. Custom profiles fall back to the trigger as description.</summary>
    private static readonly Dictionary<string, string> BuiltInDescriptions = new(StringComparer.Ordinal)
    {
        [DefaultPipelineProfiles.RegionCaptureId] =
            "Triggered by the configured hotkey or the tray Capture menu. Default order: editor → save → history → clipboard → upload → URL → toast.",
        [DefaultPipelineProfiles.ManualUploadId] =
            "Triggered by tray → Upload (Upload file… / Upload from clipboard). Skips save-to-disk and image-to-clipboard since the source is already there.",
        [DefaultPipelineProfiles.OnClipboardId] =
            "Runs every time something new lands on the system clipboard so it gets indexed in history.",
    };

    private readonly IPipelineProfileStore _profiles;
    private readonly PipelineProfileSeeder _seeder;
    private readonly HotkeyConfigService _hotkeys;

    public WorkflowsViewModel(IPipelineProfileStore profiles, PipelineProfileSeeder seeder, HotkeyConfigService hotkeys, WorkflowEditorViewModel editor, Services.LocalizationService localization)
    {
        _profiles = profiles;
        _seeder = seeder;
        _hotkeys = hotkeys;
        Editor = editor;
        Workflows = [];
        // Re-translate built-in workflow names when the user flips UI language. Custom workflows
        // (renamed by the user) keep their stored DisplayName since the helper falls back to it.
        localization.CultureChanged += async (_, _) => await ReloadWorkflowsAsync().ConfigureAwait(true);
        _ = ReloadWorkflowsAsync();
    }

    public ObservableCollection<WorkflowOption> Workflows { get; }
    public WorkflowEditorViewModel Editor { get; }

    /// <summary>Raised after a workflow is successfully deleted (only fires for custom — built-ins
    /// can't be removed). HotkeysViewModel subscribes to drop the user back to the list view if
    /// they were editing the just-deleted workflow.</summary>
    public event EventHandler<string>? WorkflowDeleted;

    /// <summary>Raised after a workflow's DisplayName is successfully renamed via the inline
    /// editor. HotkeysViewModel subscribes to refresh its row list so the new name appears
    /// without needing the user to click Back first.</summary>
    public event EventHandler<string>? WorkflowDisplayNameChanged;

    /// <summary>Raised after the user fires "Reset all to defaults". HotkeysViewModel subscribes
    /// to rebuild every chip (the per-id GetEffectiveAsync call after the reset returns the
    /// default bindings, which the existing VMs don't pick up by themselves). Distinct from
    /// <see cref="WorkflowDisplayNameChanged"/> because the reset doesn't necessarily change any
    /// display name — only steps and hotkeys.</summary>
    public event EventHandler? WorkflowsBulkReset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateWorkflowCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveWorkflowCommand))]
    private WorkflowOption? _selectedWorkflow;

    /// <summary>Mirror of <see cref="SelectedWorkflow"/>'s DisplayName, bound 2-way to the inline
    /// rename TextBox in the Hotkeys edit view. Persisted on commit via
    /// <see cref="SaveDisplayNameAsync"/> (LostFocus / Enter).</summary>
    [ObservableProperty]
    private string _editingDisplayName = string.Empty;

    private bool _suppressEditingDisplayNameSync;

    partial void OnSelectedWorkflowChanged(WorkflowOption? value)
    {
        if (value is null) return;
        _suppressEditingDisplayNameSync = true;
        EditingDisplayName = value.DisplayName;
        _suppressEditingDisplayNameSync = false;
        _ = Editor.LoadAsync(value.Id);
    }

    /// <summary>Persist the inline-edited name. Called from the TextBox's LostFocus binding —
    /// once per edit session, no debounce needed. No-ops when nothing changed.</summary>
    public async Task SaveDisplayNameAsync()
    {
        if (_suppressEditingDisplayNameSync) return;
        if (SelectedWorkflow is not { } current) return;
        var trimmed = (EditingDisplayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, current.DisplayName, StringComparison.Ordinal))
        {
            // Reject empty / unchanged. Snap the textbox back to the current name when empty.
            _suppressEditingDisplayNameSync = true;
            EditingDisplayName = current.DisplayName;
            _suppressEditingDisplayNameSync = false;
            return;
        }
        var profile = await _profiles.GetAsync(current.Id, CancellationToken.None).ConfigureAwait(true);
        if (profile is null) return;
        var updated = profile with { DisplayName = trimmed };
        await _profiles.UpsertAsync(updated, CancellationToken.None).ConfigureAwait(true);
        await ReloadWorkflowsAsync().ConfigureAwait(true);
        WorkflowDisplayNameChanged?.Invoke(this, current.Id);
    }

    /// <summary>(Re)load the workflow list from the store. Tries to keep the current selection by
    /// id; falls back to the first row when the previous selection was deleted.</summary>
    public async Task ReloadWorkflowsAsync()
    {
        var stored = await _profiles.ListAsync(CancellationToken.None).ConfigureAwait(true);
        var previousSelectedId = SelectedWorkflow?.Id;

        Workflows.Clear();
        // Localize built-in display names through WorkflowDisplayNameLocalizer; the stored
        // value (English from the seeder, or whatever the user renamed a custom workflow to)
        // is the fallback when the id isn't in the built-in map.
        var localized = stored.Select(p => (
                Profile: p,
                Display: Services.WorkflowDisplayNameLocalizer.Localize(p.Id, p.DisplayName)))
            .OrderBy(t => t.Profile.IsBuiltIn ? 0 : 1)
            .ThenBy(t => t.Display, StringComparer.OrdinalIgnoreCase);
        foreach (var (profile, display) in localized)
        {
            var description = BuiltInDescriptions.TryGetValue(profile.Id, out var d)
                ? d
                : profile.Trigger;
            Workflows.Add(new WorkflowOption(profile.Id, display, description, profile.IsBuiltIn));
        }

        var nextSelection = previousSelectedId is null
            ? null
            : Workflows.FirstOrDefault(w => w.Id == previousSelectedId);
        SelectedWorkflow = nextSelection
                       ?? Workflows.FirstOrDefault(w => w.Id == DefaultPipelineProfiles.RegionCaptureId)
                       ?? Workflows.FirstOrDefault();
    }

    public Task ReloadAsync()
    {
        var current = SelectedWorkflow ?? Workflows.FirstOrDefault();
        if (current is null) return Task.CompletedTask;
        return Editor.LoadAsync(current.Id);
    }

    [RelayCommand]
    private async Task AddWorkflowAsync()
    {
        // No modal — create with a default name and let the caller drop the user straight into
        // edit view with the inline name field selected for immediate typing. If "New workflow"
        // already exists we suffix with the next free number so the picker isn't ambiguous.
        var baseName = "New workflow";
        var existing = new HashSet<string>(Workflows.Select(w => w.DisplayName), StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        var n = 2;
        while (existing.Contains(name)) name = $"{baseName} {n++}";

        // Generate a stable, collision-free id. "custom-" prefix lets debug logs distinguish
        // user-created profiles at a glance from the kebab-case built-in ids.
        var id = $"custom-{Guid.NewGuid():N}";
        var profile = new PipelineProfile(
            Id: id,
            DisplayName: name,
            Trigger: $"hotkey:{id}",
            Steps: [],
            IsBuiltIn: false);
        await _profiles.UpsertAsync(profile, CancellationToken.None).ConfigureAwait(true);
        await ReloadWorkflowsAsync().ConfigureAwait(true);
        SelectedWorkflow = Workflows.FirstOrDefault(w => w.Id == id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DuplicateWorkflowAsync()
    {
        if (SelectedWorkflow is not { } current) return;
        var source = await _profiles.GetAsync(current.Id, CancellationToken.None).ConfigureAwait(true);
        if (source is null) return;

        var newId = $"custom-{Guid.NewGuid():N}";
        var copy = source with
        {
            Id = newId,
            DisplayName = $"{source.DisplayName} (copy)",
            Trigger = $"hotkey:{newId}",
            // Don't carry the hotkey binding to the copy — two profiles on the same combo would
            // race; user re-binds the duplicate explicitly if they want one.
            Hotkey = null,
            // The copy is always user-editable, even if cloned from a built-in.
            IsBuiltIn = false,
        };
        await _profiles.UpsertAsync(copy, CancellationToken.None).ConfigureAwait(true);
        await ReloadWorkflowsAsync().ConfigureAwait(true);
        SelectedWorkflow = Workflows.FirstOrDefault(w => w.Id == newId);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveWorkflowAsync()
    {
        if (SelectedWorkflow is not { } current) return;
        if (current.IsBuiltIn) return;
        var confirm = MessageBox.Show(
            $"Delete workflow '{current.DisplayName}'?\n\nThis can't be undone. Built-in workflows aren't affected.",
            "Delete workflow",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        // Unregister the hotkey hook BEFORE deleting the profile — once the profile is gone,
        // ClearAsync would fail to find it. NotifyHotkeyRemoved is a fire-and-forget signal.
        _hotkeys.NotifyHotkeyRemoved(current.Id);
        await _profiles.DeleteAsync(current.Id, CancellationToken.None).ConfigureAwait(true);
        await ReloadWorkflowsAsync().ConfigureAwait(true);
        WorkflowDeleted?.Invoke(this, current.Id);
    }

    /// <summary>"Reset all hotkeys" — wipes every built-in's binding back to the seeded default
    /// (unbound for most, Win+Shift+S for the canonical region capture). Custom workflows and
    /// the user's step customisations on built-ins are left untouched. Use this when a hotkey
    /// table got into a confusing state (duplicates, ghosts) but the user wants to keep their
    /// pipeline edits.</summary>
    [RelayCommand]
    private async Task ResetAllHotkeysAsync()
    {
        var confirm = MessageBox.Show(
            "Reset all hotkeys to their defaults?\n\n" +
            "Only the keyboard shortcuts get reset — your workflow steps, custom workflows, and " +
            "any settings stay as they are.",
            "Reset all hotkeys",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        foreach (var profile in DefaultPipelineProfiles.All)
        {
            // Use HotkeyConfigService.ResetAsync — writes the seeded default binding (or unbound)
            // into the profile WITHOUT touching its Steps. The Changed event fires inside the
            // service so the runtime keyboard hook re-binds automatically.
            await _hotkeys.ResetAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
        }
        WorkflowsBulkReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Reset all workflows" — overwrites every built-in profile's Steps + Hotkey with
    /// the seeded defaults. Use when a built-in's pipeline got misconfigured to the point of
    /// being unusable. Custom workflows are NOT touched.</summary>
    [RelayCommand]
    private async Task ResetAllWorkflowsAsync()
    {
        var confirm = MessageBox.Show(
            "Reset all built-in workflows to their default steps and hotkeys?\n\n" +
            "Custom workflows you've added are NOT touched. This is meant for recovering from " +
            "a built-in workflow that's been misconfigured to the point of being unusable.",
            "Reset all workflows",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        foreach (var profile in DefaultPipelineProfiles.All)
        {
            await _seeder.ResetToDefaultsAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
            // Always unregister first — the user might have bound a hotkey to a profile whose
            // default is unbound (e.g. ActiveWindowCapture). Without this their custom binding
            // would survive in the runtime hook even after the DB row got reset to null, leaving
            // a "ghost" hotkey active for a workflow whose definition no longer mentions it.
            _hotkeys.NotifyHotkeyRemoved(profile.Id);
            // Re-register only when the default profile actually has a binding. Profiles with
            // no default hotkey stay unbound after reset, matching what the user sees in the UI.
            if (profile.Hotkey is { } b)
                _hotkeys.NotifyHotkeyRebound(profile.Id, (AresToys.Hotkeys.HotkeyModifiers)b.Modifiers, b.VirtualKey);
        }
        await ReloadWorkflowsAsync().ConfigureAwait(true);
        // WorkflowOption is a record (value-equality): when ReloadWorkflowsAsync rebuilds the
        // collection with the same id+name+description+IsBuiltIn, the SelectedWorkflow setter
        // sees the new instance as equal to the old and DOESN'T fire OnSelectedWorkflowChanged,
        // which is what would normally trigger Editor.LoadAsync. Force-refresh the editor
        // explicitly so the user sees the reset steps. Same reason the Hotkeys list view rebuild
        // is signalled via the bulk-reset event below.
        if (SelectedWorkflow is not null)
            await Editor.LoadAsync(SelectedWorkflow.Id).ConfigureAwait(true);
        WorkflowsBulkReset?.Invoke(this, EventArgs.Empty);
    }

    private bool HasSelection() => SelectedWorkflow is not null;
    private bool CanRemove() => SelectedWorkflow is { IsBuiltIn: false };
}

/// <summary>One row in the Workflows list. <paramref name="IsBuiltIn"/> drives the gating of the
/// Remove command — built-ins can be reset but not deleted.</summary>
public sealed record WorkflowOption(string Id, string DisplayName, string Description, bool IsBuiltIn);
