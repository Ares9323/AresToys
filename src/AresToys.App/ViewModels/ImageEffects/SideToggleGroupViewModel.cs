namespace AresToys.App.ViewModels.ImageEffects;

/// <summary>Compass-style cluster of side-toggle parameters (Top / Right / Bottom / Left
/// + an optional centre toggle like "Curved"). Effects such as TornEdge expose these as
/// individual <see cref="bool"/> properties; rendering them in a 3×3 grid mirrors what
/// they actually mean (which side is being torn) far better than a vertical stack of
/// checkboxes.</summary>
public sealed class SideToggleGroupViewModel
{
    public EffectParameterViewModel Top { get; }
    public EffectParameterViewModel Right { get; }
    public EffectParameterViewModel Bottom { get; }
    public EffectParameterViewModel Left { get; }
    /// <summary>Optional centre chip — the only "non-compass" boolean we accept here, used
    /// for things like TornEdge.Curved that conceptually live with the side toggles even
    /// though they don't pick a direction.</summary>
    public EffectParameterViewModel? Center { get; }
    public string? CenterLabel => Center?.Label;

    public bool HasCenter => Center is not null;

    private SideToggleGroupViewModel(
        EffectParameterViewModel top,
        EffectParameterViewModel right,
        EffectParameterViewModel bottom,
        EffectParameterViewModel left,
        EffectParameterViewModel? center)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
        Center = center;
    }

    /// <summary>Build the group from the effect's parameters keyed by property name. Returns
    /// null if the effect doesn't have all four cardinal-side bools — the caller falls back
    /// to rendering them in the linear list. Marks each member's <c>IsInSideGroup</c> so the
    /// linear list collapses the rows that the compass takes over.</summary>
    public static SideToggleGroupViewModel? TryCreate(IReadOnlyDictionary<string, EffectParameterViewModel> byName)
    {
        if (!byName.TryGetValue("Top", out var top) || !top.IsBool) return null;
        if (!byName.TryGetValue("Right", out var right) || !right.IsBool) return null;
        if (!byName.TryGetValue("Bottom", out var bottom) || !bottom.IsBool) return null;
        if (!byName.TryGetValue("Left", out var left) || !left.IsBool) return null;

        // Pull a centre toggle if the effect names a known one. Curved is the only compass
        // centre we currently surface (TornEdge); any other bool stays in the linear list.
        EffectParameterViewModel? center = null;
        if (byName.TryGetValue("Curved", out var curved) && curved.IsBool) center = curved;

        top.IsInSideGroup = true;
        right.IsInSideGroup = true;
        bottom.IsInSideGroup = true;
        left.IsInSideGroup = true;
        if (center is not null) center.IsInSideGroup = true;

        return new SideToggleGroupViewModel(top, right, bottom, left, center);
    }
}
