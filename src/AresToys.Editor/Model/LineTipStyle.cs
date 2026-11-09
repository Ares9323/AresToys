namespace AresToys.Editor.Model;

/// <summary>Visual style of the cap/arrowhead drawn at the end(s) of a line or freehand
/// stroke. <see cref="ShareXCurve"/> reproduces ShareX's V with a concave curved base
/// (sits flush on the stroke); <see cref="FilledTriangle"/> is a solid isosceles triangle,
/// visually heavier.</summary>
public enum LineTipStyle
{
    ShareXCurve,
    FilledTriangle
}
