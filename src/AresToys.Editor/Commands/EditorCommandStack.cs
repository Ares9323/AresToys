using System.Collections.ObjectModel;
using AresToys.Editor.Model;

namespace AresToys.Editor.Commands;

public sealed class EditorCommandStack
{
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>True after any command has been executed (or recorded) since the last
    /// <see cref="MarkSaved"/> / <see cref="Reset"/>. Drives the "discard changes?" prompt when
    /// the user closes the editor without saving. Note: undo doesn't clear the flag — once the
    /// document has been touched we treat it as dirty even if the user undid back to original.</summary>
    public bool IsDirty { get; private set; }

    public void Execute(IEditorCommand command, ObservableCollection<Shape> shapes)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply(shapes);
        _undo.Push(command);
        _redo.Clear();
        IsDirty = true;
    }

    public bool Undo(ObservableCollection<Shape> shapes)
    {
        if (_undo.Count == 0) return false;
        var cmd = _undo.Pop();
        cmd.Undo(shapes);
        _redo.Push(cmd);
        return true;
    }

    public bool Redo(ObservableCollection<Shape> shapes)
    {
        if (_redo.Count == 0) return false;
        var cmd = _redo.Pop();
        cmd.Apply(shapes);
        _undo.Push(cmd);
        return true;
    }

    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        IsDirty = false;
    }

    /// <summary>Mark the document as saved — no pending changes the user could lose. Clears
    /// <see cref="IsDirty"/> without touching undo/redo history (so the user can still undo
    /// after a save).</summary>
    public void MarkSaved() => IsDirty = false;

    /// <summary>Record a replacement that has already been applied to the collection.
    /// Pushes onto undo so Ctrl+Z reverts to <paramref name="oldShape"/>; clears redo (linear history).</summary>
    public void RecordCommittedReplacement(Shape oldShape, Shape newShape)
    {
        ArgumentNullException.ThrowIfNull(oldShape);
        ArgumentNullException.ThrowIfNull(newShape);
        _undo.Push(new ReplaceShapeCommand(oldShape, newShape));
        _redo.Clear();
        IsDirty = true;
    }
}
