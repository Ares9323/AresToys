using System.Collections.ObjectModel;
using AresToys.Editor.Model;

namespace AresToys.Editor.Commands;

public sealed class RemoveShapesCommand : IEditorCommand
{
    private readonly IReadOnlyList<Shape> _shapes;
    private readonly List<int> _originalIndexes = [];

    public RemoveShapesCommand(IReadOnlyList<Shape> shapes)
    {
        _shapes = shapes;
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        _originalIndexes.Clear();
        // Remove highest-index first to keep lower indexes stable.
        var pairs = _shapes
            .Select(s => (Shape: s, Index: shapes.IndexOf(s)))
            .Where(p => p.Index >= 0)
            .OrderByDescending(p => p.Index)
            .ToList();
        foreach (var (_, idx) in pairs)
        {
            _originalIndexes.Insert(0, idx);
            shapes.RemoveAt(idx);
        }
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        // Re-insert in original-index ascending order.
        var ordered = _shapes
            .Select((s, i) => (Shape: s, Index: i < _originalIndexes.Count ? _originalIndexes[i] : shapes.Count))
            .OrderBy(p => p.Index)
            .ToList();
        foreach (var (s, idx) in ordered)
        {
            var insertAt = Math.Min(idx, shapes.Count);
            shapes.Insert(insertAt, s);
        }
    }
}
