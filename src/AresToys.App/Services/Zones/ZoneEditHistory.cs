namespace AresToys.App.Services.Zones;

/// <summary>Undo and redo stack of grid layouts for the zone editor. Layouts are immutable, so the history just
/// keeps the states. <see cref="Capacity"/> is the number of undo steps kept; the oldest state is dropped first.</summary>
public sealed class ZoneEditHistory
{
    private readonly List<GridLayout> _states;
    private int _index;

    public ZoneEditHistory(GridLayout initial, int capacity = 100)
    {
        ArgumentNullException.ThrowIfNull(initial);
        Capacity = Math.Max(1, capacity);
        _states = [initial];
    }

    /// <summary>Maximum number of undo steps kept.</summary>
    public int Capacity { get; }

    public GridLayout Current => _states[_index];

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index < _states.Count - 1;

    /// <summary>Raised after Push, Undo or Redo changed <see cref="Current"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Records a new state. Ignored when it equals <see cref="Current"/> by value; otherwise the redo
    /// states are discarded.</summary>
    public void Push(GridLayout next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ZoneGridEditor.SameLayout(next, Current)) return;

        if (_index < _states.Count - 1) _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        _states.Add(next);
        _index++;

        // Keep Capacity undo steps: that is Capacity + 1 states including the current one.
        int excess = _states.Count - (Capacity + 1);
        if (excess > 0)
        {
            _states.RemoveRange(0, excess);
            _index -= excess;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Steps back and returns the layout now current. Returns the current layout unchanged when there
    /// is nothing to undo (check <see cref="CanUndo"/>).</summary>
    public GridLayout Undo()
    {
        if (!CanUndo) return Current;
        _index--;
        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    /// <summary>Steps forward and returns the layout now current. Returns the current layout unchanged when
    /// there is nothing to redo (check <see cref="CanRedo"/>).</summary>
    public GridLayout Redo()
    {
        if (!CanRedo) return Current;
        _index++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }
}
