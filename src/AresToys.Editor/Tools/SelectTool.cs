using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

/// <summary>No-op selection tool for M3a — real selection/hit-testing arrives in M3b.</summary>
public sealed class SelectTool : IDrawingTool
{
    public EditorTool Kind => EditorTool.Select;
    public Shape? PreviewShape => null;

    public void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth) { }
    public void Update(double x, double y) { }
    public Shape? Commit(double x, double y) => null;
}
