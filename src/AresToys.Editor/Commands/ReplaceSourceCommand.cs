using System.Collections.ObjectModel;
using AresToys.Editor.Model;
using AresToys.Editor.ViewModels;

namespace AresToys.Editor.Commands;

/// <summary>Swap the editor's source PNG bytes wholesale — used by the Effects tool to commit
/// the rendered output of the image-effects editor (preset applied to the current screenshot)
/// back into the canvas. Shapes are untouched (they live in their own layer); only the
/// underlying bitmap changes. Undo restores the previous bytes.</summary>
public sealed class ReplaceSourceCommand : IEditorCommand
{
    private readonly EditorViewModel _vm;
    private readonly byte[] _newPng;
    private byte[]? _oldPng;

    public ReplaceSourceCommand(EditorViewModel vm, byte[] newPng)
    {
        _vm = vm;
        _newPng = newPng;
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        _ = shapes; // no shape mutations — effects only touch the source bitmap
        _oldPng = _vm.SourcePngBytes;
        _vm.SourcePngBytes = _newPng;
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        _ = shapes;
        if (_oldPng is null) return;
        _vm.SourcePngBytes = _oldPng;
    }
}
