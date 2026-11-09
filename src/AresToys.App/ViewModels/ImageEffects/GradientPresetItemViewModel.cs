using System.Windows.Media;
using AresToys.App.Services.ImageEffects;

namespace AresToys.App.ViewModels.ImageEffects;

/// <summary>UI wrapper for a <see cref="GradientPresets.Preset"/>: precomputes the WPF
/// brush so the swatch list doesn't have to rebuild it per render. Frozen and shared across
/// every list item — the preset list is static for the lifetime of the editor.</summary>
public sealed class GradientPresetItemViewModel
{
    public GradientPresets.Preset Preset { get; }
    public string Name => Preset.Name;
    public Brush Brush { get; }

    public GradientPresetItemViewModel(GradientPresets.Preset preset)
    {
        Preset = preset;
        Brush = EffectParameterViewModel.ToWpfBrush(preset.Gradient);
    }
}
