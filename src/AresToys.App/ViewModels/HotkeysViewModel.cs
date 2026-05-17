using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Hotkeys;
using AresToys.Pipeline.Profiles;

namespace AresToys.App.ViewModels;

/// <summary>
/// Backs the Settings → Hotkeys tab. Two view modes:
///   - <b>List mode</b> (default): grouped list of every workflow with a hotkey, plus an
///     "Add custom workflow" button at the bottom.
///   - <b>Edit mode</b>: a single-workflow detail view with inline rename, Duplicate / Remove /
///     Reset buttons and the pipeline-step editor. Triggered by clicking Edit on a row; the
///     Back button returns to list mode.
///
/// The edit view is the spiritual successor of the old standalone Workflows tab — folded in
/// here so the user has one entry point for everything workflow-related.
/// </summary>
public sealed partial class HotkeysViewModel : ObservableObject
{
    private readonly HotkeyConfigService _config;
    private readonly WorkflowsViewModel _workflows;

    public HotkeysViewModel(HotkeyConfigService config, WorkflowsViewModel workflows, AresToys.App.Services.LocalizationService localization)
    {
        _config = config;
        _workflows = workflows;
        BuiltInItems = [];
        BuiltInGroups = [];
        CustomItems = [];
        // Re-render rows + group headers when the user flips language so the localized
        // built-in display names + category titles reflect the new culture without restart.
        localization.CultureChanged += async (_, _) => await ReloadAsync().ConfigureAwait(true);
        // When a workflow is deleted (only customs can be), bail out of edit view back to the
        // list — otherwise we'd be sitting on a stale editor for a profile that's gone.
        _workflows.WorkflowDeleted += async (_, _) =>
        {
            IsEditingWorkflow = false;
            await ReloadAsync().ConfigureAwait(true);
        };
        // Inline rename in edit view: refresh the row list so the new DisplayName shows when the
        // user clicks Back (or even live, if they switch back to the list manually).
        _workflows.WorkflowDisplayNameChanged += async (_, _) => await ReloadAsync().ConfigureAwait(true);
        // Bulk reset (Reset all to defaults button): every chip's binding may have flipped, the
        // inline edit chip too. Rebuild both surfaces so the UI matches whatever the seeder just
        // wrote back to the store. Without this the chips keep showing the user's pre-reset
        // bindings even though the runtime hook has already swapped.
        _workflows.WorkflowsBulkReset += async (_, _) =>
        {
            await ReloadAsync().ConfigureAwait(true);
            await RefreshEditingWorkflowHotkeyAsync().ConfigureAwait(true);
        };
        // Keep the inline rebind widget in sync with whatever workflow is currently selected for
        // editing. Two triggers: SelectedWorkflow changes (the user clicked Duplicate or Add and
        // landed on a new row) AND IsEditingWorkflow flips on (entering edit mode in the first
        // place). PropertyChanged is the catch-all that covers both.
        _workflows.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowsViewModel.SelectedWorkflow))
                await RefreshEditingWorkflowHotkeyAsync().ConfigureAwait(true);
        };
        _ = ReloadAsync();
    }

    /// <summary>Flat list of every built-in hotkey-able workflow. Kept for callers that want a
    /// non-grouped view (search, debug). The XAML list view binds to <see cref="BuiltInGroups"/>
    /// instead so the user sees the categorised layout.</summary>
    public ObservableCollection<HotkeyItemViewModel> BuiltInItems { get; }

    /// <summary>Same items as <see cref="BuiltInItems"/> but grouped by category (Capture, Upload,
    /// Clipboard, Tools, …). Kept for callers that want a single grouped collection. The Settings
    /// UI binds to the per-category collections (<see cref="CaptureItems"/>, …) instead because
    /// the TabControl needs a separate ItemsSource per tab.</summary>
    public ObservableCollection<HotkeyCategoryGroup> BuiltInGroups { get; }

    /// <summary>Per-category collections backing the Settings → Hotkeys tabbed view. Each TabItem
    /// in the XAML binds to one of these so the "scroll forever" problem of a single flat list
    /// goes away — the user clicks Capture / Clipboard / Upload / Tools and sees only that
    /// category. Custom workflows live in <see cref="CustomItems"/>, rendered in a separate tab.
    /// All four are rebuilt by <see cref="RebuildBuiltInGroups"/>.</summary>
    public ObservableCollection<HotkeyItemViewModel> CaptureItems { get; } = [];
    public ObservableCollection<HotkeyItemViewModel> ClipboardItems { get; } = [];
    public ObservableCollection<HotkeyItemViewModel> UploadItems { get; } = [];
    public ObservableCollection<HotkeyItemViewModel> ToolsItems { get; } = [];
    public ObservableCollection<HotkeyItemViewModel> WormholeItems { get; } = [];

    public ObservableCollection<HotkeyItemViewModel> CustomItems { get; }

    /// <summary>The HotkeyItemViewModel for the workflow currently in edit view. Lets the edit
    /// pane render the same Rebind widget that lives on the list rows, so the user doesn't have
    /// to bounce back to the list to reassign the keybind. Null when no workflow is selected.
    /// Refresh callback kicks <see cref="ReloadAsync"/> so the list view's binding chip reflects
    /// the new combo immediately on Back.</summary>
    [ObservableProperty]
    private HotkeyItemViewModel? _editingWorkflowHotkey;

    /// <summary>Pass-through to the workflows view model so the edit view can bind to the same
    /// SelectedWorkflow / EditingDisplayName / Editor surface that used to live in its own tab.</summary>
    public WorkflowsViewModel Workflows => _workflows;

    /// <summary>True when the user has clicked Edit on a row → the list view is hidden and the
    /// edit view replaces it. Back returns to false.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackToListCommand))]
    private bool _isEditingWorkflow;

    /// <summary>Raised by the "+ Add custom workflow" button. Caller (Settings VM) runs the
    /// Workflows.AddWorkflowCommand and then enters edit mode on the new row.</summary>
    public event EventHandler? AddCustomWorkflowRequested;

    /// <summary>Raised right after we enter edit mode on a freshly-created workflow — the window
    /// listens to focus + SelectAll the inline name TextBox so the user can rename by just
    /// typing. Plain Edit clicks on existing rows don't fire this; they leave the text alone.</summary>
    public event EventHandler? EditNameFocusRequested;

    public async Task ReloadAsync()
    {
        BuiltInItems.Clear();
        CustomItems.Clear();
        var catalog = await _config.GetCatalogAsync(CancellationToken.None).ConfigureAwait(true);
        var sorted = catalog
            .Select(e => (Entry: e, Display: AresToys.App.Services.WorkflowDisplayNameLocalizer.Localize(e.Id, e.DisplayName)))
            .OrderBy(p => p.Display, StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, display) in sorted)
        {
            var current = await _config.GetEffectiveAsync(entry.Id, CancellationToken.None).ConfigureAwait(true);
            var item = new HotkeyItemViewModel(
                entry.Id,
                display,
                entry.IsBuiltIn,
                current,
                _config,
                refreshList: RefreshNoOp,
                openInWorkflows: id => _ = BeginEditAsync(id));
            (entry.IsBuiltIn ? BuiltInItems : CustomItems).Add(item);
        }
        RebuildBuiltInGroups();
    }

    /// <summary>Order in which categories render. Anything not listed is appended at the end in
    /// alphabetical order (so a future built-in we forget to categorise still shows up).</summary>
    private static readonly string[] CategoryOrder = ["Capture", "Clipboard", "Upload", "Tools", "Wormholes"];

    private void RebuildBuiltInGroups()
    {
        BuiltInGroups.Clear();
        CaptureItems.Clear();
        ClipboardItems.Clear();
        UploadItems.Clear();
        ToolsItems.Clear();
        WormholeItems.Clear();

        // Bucket every built-in item by its category. Items whose id isn't in the map fall under
        // DefaultCategory so we never silently drop a workflow from the UI.
        var byCategory = new Dictionary<string, List<HotkeyItemViewModel>>(StringComparer.Ordinal);
        foreach (var item in BuiltInItems)
        {
            var category = DefaultPipelineProfiles.CategoriesById.TryGetValue(item.Id, out var c)
                ? c
                : DefaultPipelineProfiles.DefaultCategory;
            if (!byCategory.TryGetValue(category, out var list))
            {
                list = [];
                byCategory[category] = list;
            }
            list.Add(item);
        }

        // Pour the bucketed items into the per-tab collections the Settings UI binds to.
        // Anything in an unknown category gets dropped into Tools as the catch-all — better than
        // a hidden tab that quietly grows with new built-ins we forgot to map.
        if (byCategory.TryGetValue("Capture", out var cap)) foreach (var i in cap) CaptureItems.Add(i);
        if (byCategory.TryGetValue("Clipboard", out var clip)) foreach (var i in clip) ClipboardItems.Add(i);
        if (byCategory.TryGetValue("Upload", out var up)) foreach (var i in up) UploadItems.Add(i);
        if (byCategory.TryGetValue("Tools", out var tools)) foreach (var i in tools) ToolsItems.Add(i);
        if (byCategory.TryGetValue("Wormholes", out var wh)) foreach (var i in wh) WormholeItems.Add(i);
        foreach (var (cat, list) in byCategory)
        {
            if (cat is "Capture" or "Clipboard" or "Upload" or "Tools" or "Wormholes") continue;
            foreach (var i in list) ToolsItems.Add(i);
        }

        // Also keep BuiltInGroups populated for callers that want the grouped view (Reset, debug).
        var orderedCategories = CategoryOrder
            .Where(byCategory.ContainsKey)
            .Concat(byCategory.Keys.Where(k => !CategoryOrder.Contains(k, StringComparer.Ordinal))
                                   .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        foreach (var category in orderedCategories)
        {
            // Localized header — the bucketing key stays in English ("Capture" / "Upload" / …)
            // so DefaultPipelineProfiles.CategoriesById keeps working as-is; only the human-facing
            // group title is swapped through the resx lookup.
            var group = new HotkeyCategoryGroup(AresToys.App.Services.WorkflowDisplayNameLocalizer.LocalizeCategory(category));
            foreach (var item in byCategory[category])
                group.Items.Add(item);
            BuiltInGroups.Add(group);
        }
    }

    /// <summary>Enter edit mode on the workflow with the given id. Selects it in the underlying
    /// WorkflowsViewModel so the editor + inline rename + Duplicate/Remove/Reset all bind to
    /// the right profile. Set <paramref name="focusName"/> when entering edit on a newly-created
    /// workflow — the window will focus the inline name field and SelectAll so the user can
    /// type the real name immediately without first clearing the placeholder.</summary>
    public async Task BeginEditAsync(string id, bool focusName = false)
    {
        var match = _workflows.Workflows.FirstOrDefault(w => w.Id == id);
        if (match is null)
        {
            // The workflow list might be stale (e.g. just added). Refresh and retry once.
            await _workflows.ReloadWorkflowsAsync().ConfigureAwait(true);
            match = _workflows.Workflows.FirstOrDefault(w => w.Id == id);
            if (match is null) return;
        }
        _workflows.SelectedWorkflow = match;
        IsEditingWorkflow = true;
        // Always refresh the inline hotkey VM here — not just on SelectedWorkflow PropertyChanged.
        // If the user assigned a hotkey from the list view (which writes to the config but doesn't
        // touch SelectedWorkflow) and then opens the same workflow's editor, the property setter
        // above is a no-op (already selected) so the PropertyChanged-listener path skips. Explicit
        // refresh on every BeginEdit keeps the editor chip in sync with whatever's persisted.
        await RefreshEditingWorkflowHotkeyAsync().ConfigureAwait(true);
        if (focusName) EditNameFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>(Re)build the inline rebind VM for whatever <see cref="WorkflowsViewModel.SelectedWorkflow"/>
    /// currently points at. Called on selection change and after rebinds. Falls back to null when
    /// no workflow is selected (initial load before the list renders).</summary>
    private async Task RefreshEditingWorkflowHotkeyAsync()
    {
        var current = _workflows.SelectedWorkflow;
        if (current is null) { EditingWorkflowHotkey = null; return; }
        var binding = await _config.GetEffectiveAsync(current.Id, CancellationToken.None).ConfigureAwait(true);
        EditingWorkflowHotkey = new HotkeyItemViewModel(
            current.Id,
            current.DisplayName,
            current.IsBuiltIn,
            binding,
            _config,
            // Refresh kicks ReloadAsync so the list view's chip reflects the new combo when the
            // user heads Back. Fire-and-forget — the rebind dialog has already closed by the time
            // this runs and the user can't observe a torn state.
            refreshList: () => _ = ReloadAsync(),
            // Already in edit view — clicking "Edit" inside the inline widget would be a no-op,
            // so wire the callback to do nothing rather than trying to re-enter the same view.
            openInWorkflows: _ => { });
    }

    private bool CanGoBack() => IsEditingWorkflow;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackToList()
    {
        // Commit any pending inline rename FIRST. The TextBox's LostFocus fires when the user
        // clicks Back / hits Esc, but it's a fire-and-forget save — without explicitly awaiting
        // here we'd race the upcoming ReloadAsync and pull stale names from the store.
        await _workflows.SaveDisplayNameAsync().ConfigureAwait(true);
        IsEditingWorkflow = false;
        // Refresh the list — the user may have renamed / removed / duplicated while editing.
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void AddCustomWorkflow() => AddCustomWorkflowRequested?.Invoke(this, EventArgs.Empty);

    private void RefreshNoOp() { /* placeholder for future global refresh hook (e.g. clear duplicate flag) */ }
}

/// <summary>One section in the categorised built-in hotkey list — a label plus the items that
/// belong to it. Mutable observable collection so future filtering / drag-reorder can edit in
/// place without recreating the group object (which would collapse-then-re-expand the visual
/// header in WPF).</summary>
public sealed class HotkeyCategoryGroup
{
    public HotkeyCategoryGroup(string category)
    {
        Category = category;
        Items = [];
    }

    public string Category { get; }
    public ObservableCollection<HotkeyItemViewModel> Items { get; }
}
