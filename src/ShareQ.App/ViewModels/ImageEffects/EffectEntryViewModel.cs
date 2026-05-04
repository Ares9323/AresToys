using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using ShareQ.ImageEffects;

namespace ShareQ.App.ViewModels.ImageEffects;

/// <summary>One row in the effects list. Mirrors a single <see cref="EffectPresetEntry"/>
/// but exposes the metadata the UI needs (display name, parameter rows, enabled toggle)
/// without the host code having to reflect on every render.</summary>
public sealed partial class EffectEntryViewModel : ObservableObject
{
    public EffectPresetEntry Entry { get; }
    /// <summary>Settable so WPF's PropertyPathWorker.CheckReadOnly doesn't flag the binding
    /// as "can't write into a read-only property" when the parent (SelectedEntry) flips to a
    /// new instance — even on bindings that are notionally OneWay, the path-traversal helper
    /// still walks the segments looking for writable members.</summary>
    public string DisplayName { get; set; }
    public ObservableCollection<EffectParameterViewModel> Parameters { get; set; } = new();

    public Action? Changed { get; set; }

    public EffectEntryViewModel(EffectPresetEntry entry)
    {
        Entry = entry;
        DisplayName = entry.Effect?.Name ?? "(empty)";
        if (entry.Effect is null) return;

        // Reflect once at construction so the property grid stays in sync with the underlying
        // effect — Step/Min/Max are pulled from EffectParameterAttribute when present, defaults
        // otherwise.
        var props = entry.Effect.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && (p.PropertyType == typeof(float)
                                                    || p.PropertyType == typeof(double)
                                                    || p.PropertyType == typeof(int)));
        foreach (var prop in props)
        {
            var pvm = new EffectParameterViewModel(entry.Effect, prop)
            {
                Changed = () => Changed?.Invoke(),
            };
            Parameters.Add(pvm);
        }
    }

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            Entry.Enabled = value;
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }
}
