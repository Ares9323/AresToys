namespace AresToys.App.Services.Zones;

/// <summary>Undo and redo stack of zone layouts (grid or canvas) for the zone editor. Layouts are immutable, so the
/// history just keeps the states. <paramref name="same"/> decides when a pushed state equals the current one.
/// <see cref="Capacity"/> is the number of undo steps kept; the oldest state is dropped first.</summary>
public sealed class ZoneEditHistory<T> where T : class
{
    private readonly List<T> _states;
    private readonly Func<T, T, bool> _same;
    private int _index;

    public ZoneEditHistory(T initial, Func<T, T, bool> same, int capacity = 100)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(same);
        _same = same;
        Capacity = Math.Max(1, capacity);
        _states = [initial];
    }

    /// <summary>Maximum number of undo steps kept.</summary>
    public int Capacity { get; }

    public T Current => _states[_index];

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index < _states.Count - 1;

    /// <summary>Raised after Push, Undo or Redo changed <see cref="Current"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Records a new state. Ignored when it equals <see cref="Current"/> by the comparer given to the
    /// constructor; otherwise the redo states are discarded.</summary>
    public void Push(T next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (_same(next, Current)) return;

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
    public T Undo()
    {
        if (!CanUndo) return Current;
        _index--;
        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    /// <summary>Steps forward and returns the layout now current. Returns the current layout unchanged when
    /// there is nothing to redo (check <see cref="CanRedo"/>).</summary>
    public T Redo()
    {
        if (!CanRedo) return Current;
        _index++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }
}
