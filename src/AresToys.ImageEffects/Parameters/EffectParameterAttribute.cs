namespace AresToys.ImageEffects.Parameters;

/// <summary>Annotates a public effect property with the metadata needed by the property
/// grid: numeric range, slider step, optional friendly label. Reflective UI generators read
/// this off the property to build the right control without each effect having to ship its
/// own ViewModel. Properties without the attribute fall back to defaults (range -100..100,
/// step 1, label = property name) — that covers ~80% of ShareX adjustments.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EffectParameterAttribute : Attribute
{
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }

    /// <summary>Human-readable label. When null, the UI uses the property name as-is.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Number of decimals shown in the slider readout. 0 (default) snaps to integers
    /// for sliders like Brightness/Contrast; floats like Gamma want 1 or 2.</summary>
    public int Decimals { get; init; }

    public EffectParameterAttribute(double min, double max, double step = 1)
    {
        Min = min;
        Max = max;
        Step = step;
    }

    /// <summary>Label-only annotation for non-numeric properties (bool / enum / string) where
    /// Min/Max/Step have no meaning. Lets `[EffectParameter(DisplayName = "...")]` rename a
    /// checkbox or combobox without inventing fake numeric ranges.</summary>
    public EffectParameterAttribute()
    {
        Min = 0;
        Max = 0;
        Step = 1;
    }
}
