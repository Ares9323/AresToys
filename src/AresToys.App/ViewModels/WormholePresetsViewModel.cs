using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Resources;
using AresToys.App.Services.Wormholes;
using AresToys.App.Views;

namespace AresToys.App.ViewModels;

/// <summary>Backs the "Layout presets" section of Settings → Wormholes. Lists saved presets,
/// shows which preset (if any) is auto-associated with the current monitor setup, and exposes
/// Save / Restore / Update / Rename / Delete commands. All persistence goes through
/// <see cref="IWormholeWindowManager"/> which also pushes geometry onto the live windows.</summary>
public sealed partial class WormholePresetsViewModel : ObservableObject
{
    private readonly IWormholeWindowManager _manager;

    public ObservableCollection<string> Presets { get; } = new();

    /// <summary>Name of the preset auto-mapped to the current monitor setup, or null when the
    /// current setup is unknown (nothing auto-applies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSetupPresetDisplay))]
    private string? _currentSetupPreset;

    /// <summary>Display form of <see cref="CurrentSetupPreset"/>: the preset name, or a localized
    /// "none" when this setup isn't mapped. Bound by the Settings label.</summary>
    public string CurrentSetupPresetDisplay =>
        string.IsNullOrEmpty(CurrentSetupPreset) ? Strings.WormholePresets_None : CurrentSetupPreset;

    public WormholePresetsViewModel(IWormholeWindowManager manager)
    {
        _manager = manager;
    }

    /// <summary>Rehydrate the preset list + current-setup mapping. Called on tab activation and
    /// after every mutating command.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var names = await _manager.ListPresetsAsync(CancellationToken.None);
            var current = await _manager.CurrentSetupPresetAsync(CancellationToken.None);
            Presets.Clear();
            foreach (var n in names) Presets.Add(n);
            CurrentSetupPreset = current;
        }
        catch
        {
            // Best-effort UI hydration; the store logs the real failure.
        }
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var dlg = new TextPromptDialog(Strings.WormholePresets_SaveTitle, Strings.WormholePresets_NameLabel);
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.Result)) return;
        var name = dlg.Result.Trim();
        // A name that matches an existing preset (case-insensitive) would silently overwrite it —
        // confirm first. "Update" on a row is the explicit overwrite path and stays confirmation-free.
        if (Presets.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
        {
            var confirm = MessageBox.Show(
                Strings.WormholePresets_OverwriteConfirm.Replace("{0}", name),
                "AresToys", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK) return;
        }
        await _manager.SaveCurrentAsPresetAsync(name, CancellationToken.None);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        await _manager.RestorePresetAsync(name, CancellationToken.None);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UpdateAsync(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        await _manager.SaveCurrentAsPresetAsync(name, CancellationToken.None);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RenameAsync(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var dlg = new TextPromptDialog(Strings.WormholePresets_RenameTitle, Strings.WormholePresets_NameLabel, name);
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.Result)) return;
        try
        {
            await _manager.RenamePresetAsync(name, dlg.Result, CancellationToken.None);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var confirm = MessageBox.Show(
            Strings.WormholePresets_DeleteConfirm.Replace("{0}", name),
            "AresToys", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;
        await _manager.DeletePresetAsync(name, CancellationToken.None);
        await RefreshAsync();
    }
}
