using System.Collections.ObjectModel;
using AresToys.Editor.Model;

namespace AresToys.Editor.Commands;

/// <summary>Right-click delete on a step counter: removes the target, then decrements
/// <see cref="StepCounterShape.Number"/> on every other counter whose number was higher,
/// so a "1, 2, 3, 4" sequence with #2 deleted becomes "1, 2, 3" instead of leaving a hole.
/// ShareX parity. The whole renumber lands as a single undo step — Undo restores the
/// removed counter at its original index AND rolls back every renumbered sibling to its
/// original number.</summary>
public sealed class DeleteStepAndRenumberCommand : IEditorCommand
{
    private readonly StepCounterShape _removed;
    private int _originalIndex = -1;

    /// <summary>Counters renumbered by Apply, captured as (originalShape, replacementShape)
    /// pairs so Undo can swap them back without recomputing. Index in the list matches the
    /// position in <see cref="ObservableCollection{T}"/> at Apply time.</summary>
    private readonly List<(StepCounterShape Original, StepCounterShape Decremented)> _renumbered = [];

    public DeleteStepAndRenumberCommand(StepCounterShape removed)
    {
        _removed = removed;
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        _originalIndex = shapes.IndexOf(_removed);
        if (_originalIndex < 0) return; // already gone — nothing to do

        // Pass 1: collect every counter whose Number > _removed.Number. Snapshot here so we
        // don't mutate while iterating, and so Undo knows which indices to touch.
        _renumbered.Clear();
        for (var i = 0; i < shapes.Count; i++)
        {
            if (shapes[i] is StepCounterShape sc && sc.Number > _removed.Number)
            {
                _renumbered.Add((sc, sc with { Number = sc.Number - 1 }));
            }
        }

        // Pass 2: swap renumbered entries in place. Identity-based replacement keeps every
        // other shape position stable.
        foreach (var (orig, decremented) in _renumbered)
        {
            var idx = shapes.IndexOf(orig);
            if (idx >= 0) shapes[idx] = decremented;
        }

        // Pass 3: remove the target itself. Done last so the index captured in Pass 1
        // stays valid through the renumber loop above.
        shapes.RemoveAt(_originalIndex);
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        if (_originalIndex < 0) return;

        // Re-insert the removed counter at its original index. Bounds-clamped in case the
        // collection has shrunk further after this command (shouldn't happen given the
        // command stack semantics, but cheap guard).
        var insertAt = Math.Min(_originalIndex, shapes.Count);
        shapes.Insert(insertAt, _removed);

        // Roll back every renumbered counter to its original Number. Look up by the
        // decremented shape (that's what's currently in the collection post-Apply).
        foreach (var (orig, decremented) in _renumbered)
        {
            var idx = shapes.IndexOf(decremented);
            if (idx >= 0) shapes[idx] = orig;
        }
    }
}
