using System.Collections.ObjectModel;
using AresToys.Editor.Model;

namespace AresToys.Editor.Commands;

public interface IEditorCommand
{
    void Apply(ObservableCollection<Shape> shapes);
    void Undo(ObservableCollection<Shape> shapes);
}
