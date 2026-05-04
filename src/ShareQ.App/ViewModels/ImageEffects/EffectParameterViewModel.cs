using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using ShareQ.ImageEffects;
using ShareQ.ImageEffects.Parameters;

namespace ShareQ.App.ViewModels.ImageEffects;

/// <summary>One row in the property grid: a single tunable property exposed by an
/// <see cref="ImageEffect"/>. The slider control binds directly to <see cref="Value"/>;
/// changing it writes back to the underlying property and notifies the host (so it can kick
/// the preview re-render).</summary>
public sealed partial class EffectParameterViewModel : ObservableObject
{
    private readonly ImageEffect _effect;
    private readonly PropertyInfo _property;

    // Settable so WPF binding-path traversal doesn't flag CheckReadOnly when the parameter
    // list is rebound (parent SelectedEntry changes). Init-once at construction.
    public string Label { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; }
    public string ValueFormat { get; set; }

    public Action? Changed { get; set; }

    [ObservableProperty]
    private double _value;

    public EffectParameterViewModel(ImageEffect effect, PropertyInfo property)
    {
        _effect = effect;
        _property = property;
        var attr = property.GetCustomAttribute<EffectParameterAttribute>();
        Label = attr?.DisplayName ?? property.Name;
        Min = attr?.Min ?? -100;
        Max = attr?.Max ?? 100;
        Step = attr?.Step ?? 1;
        ValueFormat = (attr?.Decimals ?? 0) > 0
            ? $"F{attr!.Decimals}"
            : "F0";

        var current = property.GetValue(effect);
        _value = current is null ? 0 : System.Convert.ToDouble(current, System.Globalization.CultureInfo.InvariantCulture);
    }

    partial void OnValueChanged(double value)
    {
        // Cast back to the property's CLR type. Effects use float almost universally; ints and
        // doubles are tolerated through Convert.ChangeType so an integer parameter still
        // round-trips cleanly through the double-typed Slider.
        var typed = System.Convert.ChangeType(value, _property.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
        _property.SetValue(_effect, typed);
        Changed?.Invoke();
    }
}
