using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace AresToys.App.ViewModels;

/// <summary>
/// Edits the step list of a single pipeline profile (region-capture, manual-upload, …). The list
/// shows only steps that are actually in the profile (no synthetic defaults). User mutations —
/// add via the categorized "+ Add step" picker, remove, reorder, mute via toggle — write through
/// to <see cref="IPipelineProfileStore"/> immediately. Plumbing-tagged tasks
/// (<see cref="WorkflowActionDescriptor.IsPlumbing"/>) stay in the underlying profile but are
/// hidden from the editor.
/// </summary>
public sealed partial class WorkflowEditorViewModel : ObservableObject
{
    private readonly IPipelineProfileStore _profiles;
    private readonly PipelineProfileSeeder _seeder;
    private readonly WorkflowActionProvider _actions;

    /// <summary>Mutable mirror of the persisted profile's Steps. Mutation order: edit this list,
    /// then <see cref="SyncItemsFromStorage"/> rebuilds the UI list, then save the profile.</summary>
    private List<PipelineStep> _storage = [];
    /// <summary>Snapshot of the action catalog (static + per-uploader-enabled) loaded each time
    /// a profile is opened. Drives both the "+ Add step" picker and the per-step display name.</summary>
    private IReadOnlyList<WorkflowActionDescriptor> _descriptors = WorkflowActionCatalog.All;
    private string? _profileId;
    private bool _isReloading;

    /// <summary>True when the currently-loaded profile is a built-in. The "Reset to defaults"
    /// button binds Visibility to this — custom workflows have no seeded default so the button
    /// has nothing meaningful to do (it would crash trying to look up a non-existent default).</summary>
    [ObservableProperty]
    private bool _canResetToDefaults;

    public WorkflowEditorViewModel(IPipelineProfileStore profiles, PipelineProfileSeeder seeder, WorkflowActionProvider actions)
    {
        _profiles = profiles;
        _seeder = seeder;
        _actions = actions;
        Items = [];
        AddableActions = [];
    }

    public ObservableCollection<WorkflowStepViewModel> Items { get; }

    /// <summary>Categorized list of actions for the "+ Add step" menu (sorted by category then name).
    /// Mutated in <see cref="LoadAsync"/> when uploader plug-ins toggle on/off; the menu is built
    /// on click so it always reads the current value without needing INotifyPropertyChanged.</summary>
    public IReadOnlyList<WorkflowActionGroup> AddableActions { get; private set; }

    public string? ProfileId => _profileId;

    public async Task LoadAsync(string profileId)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        _profileId = profileId;
        _isReloading = true;
        try
        {
            // Refresh the action catalog snapshot — uploader plug-ins may have been toggled since
            // the last open. This decides what's in the picker and how each step's name renders.
            _descriptors = await _actions.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            AddableActions = _descriptors
                .Where(a => !a.IsPlumbing)
                .GroupBy(a => a.Category)
                .Select(g => new WorkflowActionGroup(g.Key, g.OrderBy(a => a.DisplayName).ToList()))
                .OrderBy(g => g.Category)
                .ToList();

            var profile = await _profiles.GetAsync(profileId, CancellationToken.None).ConfigureAwait(true);
            _storage = profile?.Steps.ToList() ?? [];
            CanResetToDefaults = profile?.IsBuiltIn ?? false;
            SyncItemsFromStorage();
        }
        finally { _isReloading = false; }
    }

    [RelayCommand]
    private async Task AddStepAsync(WorkflowActionDescriptor? descriptor)
    {
        if (_isReloading || _profileId is null || descriptor is null) return;
        var config = string.IsNullOrEmpty(descriptor.DefaultConfigJson) ? null : JsonNode.Parse(descriptor.DefaultConfigJson);
        // New steps get a Guid id so multiple instances of the same task can co-exist (e.g. "Toggle
        // incognito ON" then later "Toggle incognito OFF" in the same workflow).
        _storage.Add(new PipelineStep(descriptor.TaskId, Config: config, Id: Guid.NewGuid().ToString("N")));
        SyncItemsFromStorage();
        await PersistAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        if (_profileId is null) return;
        // Defensive guard: the button is hidden for custom workflows but a stale binding could
        // still trigger this. The seeder would throw on a non-built-in id, so refuse early.
        if (!CanResetToDefaults) return;
        var confirm = MessageBox.Show(
            $"Reset the '{_profileId}' workflow to its default steps and order?\n\nThis discards any custom additions, removals, reordering or per-step toggles you've made here.",
            "Reset workflow",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        await _seeder.ResetToDefaultsAsync(_profileId, CancellationToken.None).ConfigureAwait(true);
        await LoadAsync(_profileId).ConfigureAwait(true);
    }

    private void SyncItemsFromStorage()
    {
        Items.Clear();
        for (var i = 0; i < _storage.Count; i++)
        {
            var step = _storage[i];
            var descriptor = WorkflowActionCatalog.LookupForStep(_descriptors, step);
            if (descriptor?.IsPlumbing == true) continue; // hidden plumbing
            // Localised wrappers for the editor list. Identity (TaskId, descriptor.Category) is
            // unchanged so DataContext lookups keep working; only the rendered strings flip
            // through the resx.
            var display = Services.WorkflowActionLocalizer.LocalizeTitle(
                step.TaskId, descriptor?.DisplayName ?? step.TaskId, descriptor?.LocalizationKey);
            var description = Services.WorkflowActionLocalizer.LocalizeDescription(
                step.TaskId, descriptor?.Description ?? $"Custom task ({step.TaskId})", descriptor?.LocalizationKey);
            var category = descriptor?.Category is { } cat
                ? Services.WorkflowActionLocalizer.LocalizeCategory(cat, cat)
                : null;
            var parameter = descriptor?.IntParameter;
            var parameterValue = parameter is null
                ? 0
                : (int?)step.Config?[parameter.Key] ?? parameter.DefaultValue;
            // Build a snapshot of every bool-parameter's current value for this step. Falls back
            // to the descriptor default when the key isn't yet in step.Config.
            Dictionary<string, bool>? boolValues = null;
            if (descriptor?.BoolParameters is { Count: > 0 } bools)
            {
                boolValues = new Dictionary<string, bool>(bools.Count);
                foreach (var bp in bools)
                {
                    boolValues[bp.Key] = (bool?)step.Config?[bp.Key] ?? bp.DefaultValue;
                }
            }
            // Snapshot string parameters the same way as bools — fall back to the descriptor
            // default when the key isn't yet in step.Config (newly added step, first edit).
            Dictionary<string, string>? stringValues = null;
            if (descriptor?.StringParameters is { Count: > 0 } strings)
            {
                stringValues = new Dictionary<string, string>(strings.Count);
                foreach (var sp in strings)
                {
                    stringValues[sp.Key] = (string?)step.Config?[sp.Key] ?? sp.DefaultValue;
                }
            }

            // Seed missing defaults into step.Config so the runtime task reads what the user
            // sees in the dropdown. Without this, a built-in profile seeded before the catalog
            // grew a parameter would render the descriptor's DefaultValue in the UI but
            // step.Config would still lack the key — the task then falls back to its own
            // hardcoded default (or worse, to "last used" semantics) and the user-visible
            // value disagrees with the executed value. Seeding once at sync time also covers
            // the case where the user picks the already-shown value from the combo: that
            // selection doesn't fire PropertyChanged because Value is unchanged.
            if (descriptor is not null
                && (descriptor.BoolParameters is { Count: > 0 } || descriptor.StringParameters is { Count: > 0 }))
            {
                var configObj = step.Config as JsonObject ?? (step.Config is null ? new JsonObject() : null);
                var changed = false;
                if (configObj is not null && step.Config is null) changed = true;
                if (configObj is not null && descriptor.BoolParameters is { } bps)
                {
                    foreach (var bp in bps)
                    {
                        if (configObj.ContainsKey(bp.Key)) continue;
                        configObj[bp.Key] = JsonValue.Create(boolValues![bp.Key]);
                        changed = true;
                    }
                }
                if (configObj is not null && descriptor.StringParameters is { } sps)
                {
                    foreach (var sp in sps)
                    {
                        if (configObj.ContainsKey(sp.Key)) continue;
                        configObj[sp.Key] = JsonValue.Create(stringValues![sp.Key]);
                        changed = true;
                    }
                }
                if (changed)
                {
                    _storage[i] = step with { Config = configObj };
                    step = _storage[i];
                    _pendingSeedPersist = true;
                }
            }
            // Localise parameter labels. The catalog entries are immutable records, so we
            // build a fresh collection where each entry's Label is swapped for its translation
            // (with the original Label as fallback). Keys, defaults, picker / options stay
            // intact — only the user-facing label flips.
            var localizedBools = descriptor?.BoolParameters?.Select(bp =>
                bp with { Label = Services.WorkflowActionLocalizer.LocalizeParameter(step.TaskId, bp.Key, bp.Label) })
                .ToList();
            var localizedStrings = descriptor?.StringParameters?.Select(sp =>
                sp with { Label = Services.WorkflowActionLocalizer.LocalizeParameter(step.TaskId, sp.Key, sp.Label) })
                .ToList();
            Items.Add(new WorkflowStepViewModel(
                storageIndex: i,
                taskId: step.TaskId,
                displayName: display,
                description: description,
                category: category,
                initiallyEnabled: step.Enabled,
                parameter: parameter,
                parameterValue: parameterValue,
                boolParameters: localizedBools,
                boolParameterValues: boolValues,
                stringParameters: localizedStrings,
                stringParameterValues: stringValues,
                onEnabledChanged: (item, value)   => _ = OnEnabledChangedAsync(item, value),
                onMove:           (item, delta)   => _ = OnMoveAsync(item, delta),
                onRemove:         item            => _ = OnRemoveAsync(item),
                onParameterChanged: (item, value) => _ = OnParameterChangedAsync(item, value),
                onBoolParameterChanged: (item, key, value) => _ = OnBoolParameterChangedAsync(item, key, value),
                onStringParameterChanged: (item, key, value) => _ = OnStringParameterChangedAsync(item, key, value),
                inputs: descriptor?.Inputs,
                outputs: descriptor?.Outputs,
                onDuplicate:      item            => _ = OnDuplicateAsync(item)));
        }
        UpdateMoveFlags();
        // If we filled in any missing default values into step.Config above, flush the change
        // to disk so subsequent runs (and other surfaces reading the profile straight from the
        // store) see the same values the editor renders.
        if (_pendingSeedPersist && !_isReloading && _profileId is not null)
        {
            _pendingSeedPersist = false;
            _ = PersistAsync();
        }
    }

    /// <summary>Set by <see cref="SyncItemsFromStorage"/> when it patches missing default
    /// parameter values into a step's Config. Cleared after the persist runs.</summary>
    private bool _pendingSeedPersist;

    private async Task OnEnabledChangedAsync(WorkflowStepViewModel item, bool value)
    {
        if (_isReloading || _profileId is null) return;
        if (item.StorageIndex < 0 || item.StorageIndex >= _storage.Count) return;
        _storage[item.StorageIndex] = _storage[item.StorageIndex] with { Enabled = value };
        await PersistAsync().ConfigureAwait(true);
    }

    private async Task OnParameterChangedAsync(WorkflowStepViewModel item, int value)
    {
        if (_isReloading || _profileId is null) return;
        if (item.StorageIndex < 0 || item.StorageIndex >= _storage.Count) return;
        if (item.Parameter is null) return;
        var step = _storage[item.StorageIndex];
        var config = step.Config?.DeepClone() as JsonObject ?? new JsonObject();
        config[item.Parameter.Key] = value;
        _storage[item.StorageIndex] = step with { Config = config };
        await PersistAsync().ConfigureAwait(true);
    }

    private async Task OnBoolParameterChangedAsync(WorkflowStepViewModel item, string key, bool value)
    {
        if (_isReloading || _profileId is null) return;
        if (item.StorageIndex < 0 || item.StorageIndex >= _storage.Count) return;
        var step = _storage[item.StorageIndex];
        var config = step.Config?.DeepClone() as JsonObject ?? new JsonObject();
        config[key] = value;
        _storage[item.StorageIndex] = step with { Config = config };
        await PersistAsync().ConfigureAwait(true);
    }

    private async Task OnStringParameterChangedAsync(WorkflowStepViewModel item, string key, string value)
    {
        if (_isReloading || _profileId is null) return;
        if (item.StorageIndex < 0 || item.StorageIndex >= _storage.Count) return;
        var step = _storage[item.StorageIndex];
        var config = step.Config?.DeepClone() as JsonObject ?? new JsonObject();
        config[key] = value;
        _storage[item.StorageIndex] = step with { Config = config };
        await PersistAsync().ConfigureAwait(true);
    }

    private async Task OnMoveAsync(WorkflowStepViewModel item, int delta)
    {
        if (_isReloading || _profileId is null) return;
        var src = item.StorageIndex;
        if (src < 0 || src >= _storage.Count) return;
        // Find the destination storage index by skipping over plumbing-only neighbours.
        var dst = FindUiAdjacentStorageIndex(src, delta);
        if (dst < 0) return;
        var moving = _storage[src];
        _storage.RemoveAt(src);
        // Account for index shift after removal.
        if (dst > src) dst--;
        _storage.Insert(dst, moving);
        SyncItemsFromStorage();
        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>Reorder by dropping <paramref name="source"/> next to <paramref name="target"/>.
    /// When <paramref name="insertAfter"/> is true the source lands immediately below the target,
    /// otherwise above. Both arguments are UI rows from <see cref="Items"/>; the editor maps
    /// them back to storage indices and persists.</summary>
    public async Task MoveToAsync(WorkflowStepViewModel source, WorkflowStepViewModel target, bool insertAfter)
    {
        if (_isReloading || _profileId is null) return;
        if (ReferenceEquals(source, target)) return;
        var src = source.StorageIndex;
        var dst = target.StorageIndex;
        if (src < 0 || src >= _storage.Count) return;
        if (dst < 0 || dst >= _storage.Count) return;

        var moving = _storage[src];
        _storage.RemoveAt(src);
        // After RemoveAt, indices > src shift left by one. Translate the target row's storage
        // index into the post-removal coordinate system, then add 1 if dropping below it.
        if (dst > src) dst--;
        if (insertAfter) dst++;
        if (dst < 0) dst = 0;
        if (dst > _storage.Count) dst = _storage.Count;
        _storage.Insert(dst, moving);
        SyncItemsFromStorage();
        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>True when the drag's drop position is "after the last row". Bound by the footer
    /// gutter element so the user sees the same single-line indicator at the bottom of the list.</summary>
    [ObservableProperty]
    private bool _isDropTargetAtEnd;

    /// <summary>Drop the source row at the very end of the storage list — used by the footer
    /// gutter when the user drops below the last row.</summary>
    public async Task MoveToEndAsync(WorkflowStepViewModel source)
    {
        if (_isReloading || _profileId is null) return;
        var src = source.StorageIndex;
        if (src < 0 || src >= _storage.Count) return;
        var moving = _storage[src];
        _storage.RemoveAt(src);
        _storage.Add(moving);
        SyncItemsFromStorage();
        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>Reset all per-row drag visual flags. Called by the window when the drag operation
    /// ends (drop or Esc) so no stale highlights linger.</summary>
    public void ClearDragVisuals()
    {
        foreach (var item in Items)
        {
            item.IsDragSource = false;
            item.IsDropTargetAbove = false;
        }
        IsDropTargetAtEnd = false;
    }

    private async Task OnRemoveAsync(WorkflowStepViewModel item)
    {
        if (_isReloading || _profileId is null) return;
        if (item.StorageIndex < 0 || item.StorageIndex >= _storage.Count) return;
        _storage.RemoveAt(item.StorageIndex);
        SyncItemsFromStorage();
        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>Insert a duplicate of <paramref name="item"/> immediately after it in the
    /// storage list. The clone keeps the same TaskId + Enabled flag and gets a deep copy of the
    /// Config JsonNode (so editing one step's params doesn't bleed back into its twin). Fresh
    /// Guid for the step Id so multi-step workflows referencing both copies stay unambiguous.
    /// Mirrors the "Duplicate Layer" pattern from Figma / Photoshop — place the new instance
    /// next to the source for immediate comparison / tweaking.</summary>
    private async Task OnDuplicateAsync(WorkflowStepViewModel item)
    {
        if (_isReloading || _profileId is null) return;
        if (item.StorageIndex < 0 || item.StorageIndex >= _storage.Count) return;
        var source = _storage[item.StorageIndex];
        // Deep-clone the config so subsequent param edits on one twin don't mutate the other.
        // JsonNode is mutable by reference; parsing the string-rendered form is the cheapest
        // safe deep-copy that doesn't require knowing the inner schema.
        var clonedConfig = source.Config is null
            ? null
            : System.Text.Json.Nodes.JsonNode.Parse(source.Config.ToJsonString());
        var copy = new AresToys.Core.Pipeline.PipelineStep(
            TaskId: source.TaskId,
            Config: clonedConfig,
            Enabled: source.Enabled,
            Id: Guid.NewGuid().ToString("N"));
        _storage.Insert(item.StorageIndex + 1, copy);
        SyncItemsFromStorage();
        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>Walk <paramref name="storageIndex"/> by <paramref name="delta"/> UI-positions
    /// (skipping plumbing steps that aren't in <see cref="Items"/>) and return the resulting
    /// storage index, or -1 if we'd run off either end.</summary>
    private int FindUiAdjacentStorageIndex(int storageIndex, int delta)
    {
        var step = delta > 0 ? 1 : -1;
        var remaining = Math.Abs(delta);
        var i = storageIndex;
        while (remaining > 0)
        {
            i += step;
            if (i < 0 || i >= _storage.Count) return -1;
            var candidate = _storage[i];
            var descriptor = WorkflowActionCatalog.LookupForStep(_descriptors,candidate);
            if (descriptor?.IsPlumbing == true) continue;
            remaining--;
        }
        return i;
    }

    private async Task PersistAsync()
    {
        if (_profileId is null) return;
        var profile = await _profiles.GetAsync(_profileId, CancellationToken.None).ConfigureAwait(true);
        if (profile is null) return;
        var updated = profile with { Steps = _storage.ToList() };
        await _profiles.UpsertAsync(updated, CancellationToken.None).ConfigureAwait(true);
    }

    private void UpdateMoveFlags()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].CanMoveUp = i > 0;
            Items[i].CanMoveDown = i < Items.Count - 1;
        }
    }
}

public sealed record WorkflowActionGroup(string Category, IReadOnlyList<WorkflowActionDescriptor> Actions);
