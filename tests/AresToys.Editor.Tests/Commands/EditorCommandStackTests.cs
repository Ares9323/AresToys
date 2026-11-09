using System.Collections.ObjectModel;
using AresToys.Editor.Commands;
using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Commands;

public class EditorCommandStackTests
{
    private static RectangleShape Rect(double x, double y) =>
        new(x, y, 10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);

    [Fact]
    public void Execute_AddCommand_AddsShapeToCollection()
    {
        var shapes = new ObservableCollection<Shape>();
        var stack = new EditorCommandStack();
        var r = Rect(0, 0);

        stack.Execute(new AddShapeCommand(r), shapes);

        Assert.Single(shapes);
        Assert.Equal(r, shapes[0]);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Undo_RemovesLastAdded()
    {
        var shapes = new ObservableCollection<Shape>();
        var stack = new EditorCommandStack();
        stack.Execute(new AddShapeCommand(Rect(0, 0)), shapes);
        stack.Execute(new AddShapeCommand(Rect(20, 20)), shapes);

        Assert.True(stack.Undo(shapes));

        Assert.Single(shapes);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesLastUndo()
    {
        var shapes = new ObservableCollection<Shape>();
        var stack = new EditorCommandStack();
        stack.Execute(new AddShapeCommand(Rect(0, 0)), shapes);
        stack.Undo(shapes);

        Assert.True(stack.Redo(shapes));

        Assert.Single(shapes);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Execute_AfterUndo_ClearsRedoStack()
    {
        var shapes = new ObservableCollection<Shape>();
        var stack = new EditorCommandStack();
        stack.Execute(new AddShapeCommand(Rect(0, 0)), shapes);
        stack.Undo(shapes);
        Assert.True(stack.CanRedo);

        stack.Execute(new AddShapeCommand(Rect(20, 20)), shapes);

        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Replace_RoundTripsViaUndoRedo()
    {
        var shapes = new ObservableCollection<Shape>();
        var stack = new EditorCommandStack();
        var original = Rect(0, 0);
        var modified = Rect(0, 0) with { Outline = ShapeColor.Black };

        stack.Execute(new AddShapeCommand(original), shapes);
        stack.Execute(new ReplaceShapeCommand(original, modified), shapes);
        Assert.Equal(modified, shapes[0]);

        stack.Undo(shapes);
        Assert.Equal(original, shapes[0]);

        stack.Redo(shapes);
        Assert.Equal(modified, shapes[0]);
    }
}
