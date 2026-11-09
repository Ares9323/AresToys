namespace AresToys.Editor.Model;

/// <summary>Base record for editor primitives. Concrete shapes derive (e.g., <see cref="RectangleShape"/>).</summary>
public abstract record Shape(ShapeColor Outline, ShapeColor Fill, double StrokeWidth);
