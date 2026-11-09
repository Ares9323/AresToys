namespace AresToys.Editor.Adorners;

public enum GripKind
{
    None,
    // 8-grip resize for rect/ellipse
    TopLeft, Top, TopRight,
    Left, Right,
    BottomLeft, Bottom, BottomRight,
    // 2-grip endpoint for line/arrow
    From, To,
    // Mid-point control handle on line/arrow — drag to bend the segment into a quadratic bezier.
    Bend,
    // Single grip for text font-size / stepcounter radius
    Resize,
    // Rotation grip floating above the shape (rect / ellipse / text)
    Rotate,
    // Step-counter tail tip — drag to point the wedge at a feature in the underlying image
    // (ShareX parity). Only exposed by StepCounterShape; never rotated since step counters
    // don't have a Rotation field of their own.
    Tail
}
