using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.ImageEffects;

namespace AresToys.App.ViewModels.ImageEffects;

/// <summary>UI-side wrapper around <see cref="EffectPreset"/>. Adds the inline-rename machinery
/// (snapshot/commit/cancel + IsEditing flag) without polluting the pure-domain
/// <see cref="EffectPreset"/> with INotifyPropertyChanged + INPC. The ListBox binds to a
/// collection of these; the ViewModel reaches the underlying domain object through
/// <see cref="Preset"/>.</summary>
public sealed partial class PresetItemViewModel : ObservableObject
{
    public EffectPreset Preset { get; }

    /// <summary>Fired after a successful rename commit. The host viewmodel uses this to
    /// trigger a persist + status-text update without having to subscribe to PropertyChanged
    /// on every preset.</summary>
    public Action? Renamed { get; set; }

    private string? _nameSnapshot;

    public PresetItemViewModel(EffectPreset preset)
    {
        Preset = preset;
    }

    public string Id => Preset.Id;

    public string Name
    {
        get => Preset.Name;
        set
        {
            if (Preset.Name == value) return;
            Preset.Name = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool _isEditing;

    /// <summary>Snapshot the current name so an Escape press can roll back, then flip to
    /// edit mode. The XAML template watches IsEditing to show the TextBox.</summary>
    public void BeginEdit()
    {
        if (IsEditing) return;
        _nameSnapshot = Preset.Name;
        IsEditing = true;
    }

    public void CommitEdit()
    {
        if (!IsEditing) return;
        IsEditing = false;
        // Empty names break the ListBox display and the file-name fallback when exporting,
        // so refuse blanks by reverting to the snapshot.
        if (string.IsNullOrWhiteSpace(Preset.Name))
        {
            Name = _nameSnapshot ?? "Preset";
        }
        Renamed?.Invoke();
    }

    public void CancelEdit()
    {
        if (!IsEditing) return;
        if (_nameSnapshot is not null) Name = _nameSnapshot;
        IsEditing = false;
    }
}
