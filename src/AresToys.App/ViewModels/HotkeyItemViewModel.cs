using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Hotkeys;
using AresToys.App.Views;
using AresToys.Hotkeys;

namespace AresToys.App.ViewModels;

public sealed partial class HotkeyItemViewModel : ObservableObject
{
    private readonly HotkeyConfigService _config;
    private readonly Action _refreshList;
    private readonly Action<string> _openInWorkflows;

    public HotkeyItemViewModel(
        string id,
        string displayName,
        bool isBuiltIn,
        HotkeyDefinition current,
        HotkeyConfigService config,
        Action refreshList,
        Action<string> openInWorkflows)
    {
        Id = id;
        DisplayName = displayName;
        IsBuiltIn = isBuiltIn;
        _config = config;
        _refreshList = refreshList;
        _openInWorkflows = openInWorkflows;
        UpdateBindingDisplay(current);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public bool IsBuiltIn { get; }

    /// <summary>Reset is meaningful only for built-ins (which have a seeded default); on a custom
    /// workflow there's nothing to "reset to", so we hide the button.</summary>
    public bool CanReset => IsBuiltIn;

    [ObservableProperty]
    private string _bindingDisplay = string.Empty;

    /// <summary>True when the workflow has an actual hotkey bound. False = unbound (the chip
    /// shows "Click to set hotkey…"). Bound to a DataTrigger in HotkeyChipButtonStyle so the
    /// chip foreground dims for unbound rows — otherwise the placeholder text would visually
    /// compete with real shortcuts and the user can't tell at a glance which workflows are live.</summary>
    [ObservableProperty]
    private bool _isAssigned;

    private void UpdateBindingDisplay(HotkeyDefinition def)
    {
        BindingDisplay = HotkeyDisplay.Format(def.Modifiers, def.VirtualKey);
        IsAssigned = def.Modifiers != HotkeyModifiers.None || def.VirtualKey != 0;
    }

    [RelayCommand]
    private async Task Rebind()
    {
        var dialog = new HotkeyCaptureWindow(canReset: IsBuiltIn)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        var ok = dialog.ShowDialog();
        if (ok != true) return;

        // Three exit paths from the dialog: a captured combo (Update), Clear, or Reset (built-ins
        // only). Branch here so each one routes to the matching service call.
        if (dialog.ResetRequested)
        {
            await _config.ResetAsync(Id, CancellationToken.None).ConfigureAwait(true);
        }
        else if (dialog.ClearRequested)
        {
            await _config.ClearAsync(Id, CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            // Duplicate-binding guard: if another workflow already owns this combo, ask the user
            // how to resolve. Two workflows on the same hotkey would race in the runtime hook
            // (last Register wins, the loser becomes a ghost binding). Three outcomes:
            //  - Yes  → clear the conflicting workflow first, then assign here.
            //  - No   → abort the rebind, leave both bindings untouched.
            //  - (Cancel is not offered — same effect as No.)
            var owner = await _config.FindOwnerAsync(
                dialog.CapturedModifiers, dialog.CapturedVirtualKey, excludeId: Id, CancellationToken.None)
                .ConfigureAwait(true);
            if (owner is { } conflict)
            {
                var comboLabel = HotkeyDisplay.Format(dialog.CapturedModifiers, dialog.CapturedVirtualKey);
                var pick = System.Windows.MessageBox.Show(
                    $"'{comboLabel}' is already assigned to '{conflict.DisplayName}'.\n\n" +
                    $"Clear it from '{conflict.DisplayName}' and assign it here?",
                    "Hotkey already in use",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No);
                if (pick != System.Windows.MessageBoxResult.Yes) return;
                await _config.ClearAsync(conflict.Id, CancellationToken.None).ConfigureAwait(true);
            }
            await _config.UpdateAsync(Id, dialog.CapturedModifiers, dialog.CapturedVirtualKey, CancellationToken.None).ConfigureAwait(true);
        }
        // Re-read whatever ended up persisted so the row's BindingDisplay matches reality.
        var current = await _config.GetEffectiveAsync(Id, CancellationToken.None).ConfigureAwait(true);
        UpdateBindingDisplay(current);
        _refreshList();
    }

    [RelayCommand]
    private void OpenInWorkflows() => _openInWorkflows(Id);
}
