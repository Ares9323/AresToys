using System.Collections.ObjectModel;
using AresToys.Editor.Model;

namespace AresToys.Editor.Commands;

public sealed class AddShapeCommand : IEditorCommand
{
    private readonly Shape _shape;

    public AddShapeCommand(Shape shape) { _shape = shape; }

    public void Apply(ObservableCollection<Shape> shapes) => shapes.Add(_shape);

    public void Undo(ObservableCollection<Shape> shapes)
    {
        var idx = shapes.IndexOf(_shape);
        if (idx >= 0) shapes.RemoveAt(idx);
    }
}
