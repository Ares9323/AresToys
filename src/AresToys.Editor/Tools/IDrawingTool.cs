using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

/// <summary>Stateful tool that observes mouse events and may produce a single <see cref="Shape"/>.</summary>
public interface IDrawingTool
{
    EditorTool Kind { get; }

    /// <summary>Begin a new gesture at the given canvas-coords point.</summary>
    void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth);

    /// <summary>Update the in-progress shape; <see cref="PreviewShape"/> reflects the new state.</summary>
    void Update(double x, double y);

    /// <summary>Finalize the gesture; if a complete shape exists, it is returned (and Preview cleared).</summary>
    Shape? Commit(double x, double y);

    /// <summary>The currently-being-drawn shape, or null when idle.</summary>
    Shape? PreviewShape { get; }
}
