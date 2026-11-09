using System.Collections.ObjectModel;
using AresToys.Editor.Model;
using AresToys.Editor.ViewModels;

namespace AresToys.Editor.Commands;

/// <summary>Records the addition of a pending crop rectangle so Ctrl+Z removes it. The
/// IEditorCommand interface is keyed on the Shapes collection (which the stack passes in),
/// but pending crops live on the VM under their own list — we ignore the shapes argument
/// and operate on <see cref="EditorViewModel.PendingCrops"/> directly. Same approach
/// <see cref="MultiCropCommand"/> uses for VM-level state.
/// <para>The command captures the rect by value at creation time, so even if the live
/// PendingCrops list is mutated between Apply and Undo (e.g. user resizes the rect after
/// dropping it) the undo step still removes the right entry — by reference equality on
/// the recorded rect record. Resize/move are not recorded as separate undo steps yet; that
/// would require capturing pre-state on every drag commit, which is a bigger refactor.</para></summary>
public sealed class AddPendingCropCommand : IEditorCommand
{
    private readonly EditorViewModel _vm;
    private readonly CropRect _rect;
    public AddPendingCropCommand(EditorViewModel vm, CropRect rect)
    {
        _vm = vm;
        _rect = rect;
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        // Idempotent on Redo: if the rect is somehow already present (shouldn't happen on
        // a clean stack but defensive against double-applies), don't add a duplicate.
        if (!_vm.PendingCrops.Contains(_rect)) _vm.PendingCrops.Add(_rect);
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        _vm.PendingCrops.Remove(_rect);
    }
}
