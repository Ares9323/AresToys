using System.Collections.ObjectModel;
using AresToys.Editor.Model;

namespace AresToys.Editor.Commands;

public sealed class ReplaceShapeCommand : IEditorCommand
{
    private readonly Shape _oldShape;
    private readonly Shape _newShape;

    public ReplaceShapeCommand(Shape oldShape, Shape newShape)
    {
        _oldShape = oldShape;
        _newShape = newShape;
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        var idx = shapes.IndexOf(_oldShape);
        if (idx >= 0) shapes[idx] = _newShape;
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        var idx = shapes.IndexOf(_newShape);
        if (idx >= 0) shapes[idx] = _oldShape;
    }
}
