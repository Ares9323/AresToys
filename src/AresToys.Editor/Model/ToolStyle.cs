using AresToys.Editor.Tools;

namespace AresToys.Editor.Model;

/// <summary>The complete look a placement tool gives its next shape. One record serves every
/// tool and each tool reads only its own fields: shape tools use Outline/Fill/StrokeWidth, Line,
/// Arrow and Freehand add the caps and tip style, Freehand adds Smooth, Text uses Text plus the
/// text outline, StepCounter the Step* font fields, and the effect tools take their amount from
/// <see cref="EffectAmount"/> (blur radius, pixelate block size, spotlight dim) and
/// <see cref="EffectAmount2"/> (spotlight blur radius). Value equality is what the Recent list
/// uses to spot a style that was already placed.</summary>
public sealed record ToolStyle(
    ShapeColor Outline,
    ShapeColor Fill,
    double StrokeWidth,
    bool StartCap,
    bool EndCap,
    LineTipStyle TipStyle,
    bool Smooth,
    TextStyle Text,
    ShapeColor TextOutlineColor,
    double TextOutlineWidth,
    double EffectAmount,
    double EffectAmount2,
    string StepFontFamily,
    bool StepBold,
    bool StepItalic)
{
    private static readonly ToolStyle Base = new(
        ShapeColor.Red, ShapeColor.Transparent, 2,
        StartCap: false, EndCap: false, LineTipStyle.ShareXCurve,
        Smooth: true,
        TextStyle.Default, ShapeColor.Black, 0,
        EffectAmount: 0, EffectAmount2: 0,
        "Segoe UI", StepBold: true, StepItalic: false);

    /// <summary>The style a tool starts with when nothing has been saved: the values the editor
    /// used before styles were kept per tool.</summary>
    public static ToolStyle InitialFor(EditorTool tool) => tool switch
    {
        EditorTool.Arrow => Base with { EndCap = true },
        EditorTool.Blur => Base with { EffectAmount = 12 },
        EditorTool.Pixelate => Base with { EffectAmount = 8 },
        EditorTool.Spotlight => Base with { EffectAmount = 0.5, EffectAmount2 = 0 },
        _ => Base
    };
}
