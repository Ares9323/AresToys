using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.App.Services;

namespace AresToys.App.ViewModels;

/// <summary>Brush modes for the BgRemoverWindow's manual mask refinement. <c>Add</c> paints
/// subject (force alpha towards 255 in the painted region). <c>Remove</c> paints background
/// (force alpha towards 0). Mutually exclusive — toggle the active mode via the X hotkey or
/// the radio buttons. Pan is on middle-button only since the brush is always live.</summary>
public enum BgBrushMode { Add, Remove }

/// <summary>VM for <see cref="Views.BgRemoverWindow"/>. Holds the post-processing parameters
/// (threshold / feather / edge offset) and brush state. Re-rendering is driven from the
/// codebehind (which owns the SKBitmaps) — the VM just emits PropertyChanged on slider
/// edits and the codebehind recomputes the composite.</summary>
public sealed partial class BgRemoverViewModel : ObservableObject
{
    [ObservableProperty] private int _threshold;
    [ObservableProperty] private int _featherPx;
    [ObservableProperty] private int _edgeOffsetPx;
    /// <summary>0-100% — preview-only: how visible the masked-out background is in the
    /// right-pane preview. 0 = fully transparent (final-output look). 100 = fully opaque
    /// (no cut applied visually). Lets the user see what's been cut so they can paint it
    /// back with the brush. Doesn't affect the saved PNG (Apply always uses 0).</summary>
    [ObservableProperty] private int _backgroundOpacity;

    /// <summary>Currently selected brush. Default Add; the X hotkey toggles between Add and
    /// Remove (Photoshop-style). Alt held during a stroke flips the mode for that one stroke
    /// without mutating this value.</summary>
    [ObservableProperty] private BgBrushMode _brushMode = BgBrushMode.Add;
    [ObservableProperty] private int _brushSizePx = 30;
    /// <summary>0-100 — controls edge softness of brush stamps. 100 = fully hard circle (every
    /// painted pixel has full override strength). 0 = soft Gaussian-style falloff (painted
    /// pixels at the brush edge only partially override the AI mask, blending towards it).
    /// Adjusted via Shift+Wheel.</summary>
    [ObservableProperty] private int _brushHardness = 80;

    public bool BrushIsAdd    { get => BrushMode == BgBrushMode.Add;    set { if (value) BrushMode = BgBrushMode.Add;    OnPropertyChanged(); } }
    public bool BrushIsRemove { get => BrushMode == BgBrushMode.Remove; set { if (value) BrushMode = BgBrushMode.Remove; OnPropertyChanged(); } }
    partial void OnBrushModeChanged(BgBrushMode value)
    {
        OnPropertyChanged(nameof(BrushIsAdd));
        OnPropertyChanged(nameof(BrushIsRemove));
    }

    /// <summary>Status line shown in the footer ("Running AI…" / "Ready" / "AI not available").</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>True while the model is running so we can disable Apply / brush input on the
    /// initial inference. Once the mask is in hand the rest of the UX is local + cheap.</summary>
    [ObservableProperty] private bool _isInferenceRunning;

    public BgRemovalParams ToParams() => new(Threshold, FeatherPx, EdgeOffsetPx);
}
