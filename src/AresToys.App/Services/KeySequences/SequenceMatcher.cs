namespace AresToys.App.Services.KeySequences;

/// <summary>
/// O(1) lookup of bindings by exact sequence match. The index is a <see cref="Dictionary{TKey,TValue}"/>
/// mapping sequence string → list of bindings (multiple Replacer bindings may share a sequence —
/// the overlay lists them all). The index is swapped atomically on <see cref="Rebuild"/>: readers
/// always see a consistent snapshot via a single volatile field read.
/// </summary>
public sealed class SequenceMatcher
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SequenceBinding>> EmptyIndex
        = new Dictionary<string, IReadOnlyList<SequenceBinding>>(0, StringComparer.Ordinal);

    private volatile IReadOnlyDictionary<string, IReadOnlyList<SequenceBinding>> _index = EmptyIndex;

    /// <summary>Returns all bindings whose <see cref="SequenceBinding.Sequence"/> equals
    /// <paramref name="buffer"/> exactly. Empty if no match. Case-sensitive by design — the spec
    /// treats <c>mpg@</c> and <c>Mpg@</c> as distinct triggers.</summary>
    public IReadOnlyList<SequenceBinding> Match(string buffer)
    {
        if (string.IsNullOrEmpty(buffer)) return Array.Empty<SequenceBinding>();
        return _index.TryGetValue(buffer, out var bindings)
            ? bindings
            : Array.Empty<SequenceBinding>();
    }

    /// <summary>True if any binding's sequence equals <paramref name="buffer"/> exactly. Cheaper
    /// than calling <see cref="Match"/> just to check non-emptiness; used by the tracker's hot path
    /// to decide whether to open the overlay.</summary>
    public bool HasMatch(string buffer)
        => !string.IsNullOrEmpty(buffer) && _index.ContainsKey(buffer);

    /// <summary>Atomically swap the matcher's index. Readers calling <see cref="Match"/> on another
    /// thread see either the old or new index, never a partial state. Callers should batch as many
    /// changes as possible into a single rebuild — the swap is cheap but the dict construction is
    /// O(N bindings).</summary>
    public void Rebuild(IEnumerable<SequenceBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var built = new Dictionary<string, List<SequenceBinding>>(StringComparer.Ordinal);
        foreach (var b in bindings)
        {
            if (!built.TryGetValue(b.Sequence, out var list))
            {
                list = [];
                built[b.Sequence] = list;
            }
            list.Add(b);
        }
        // Freeze the inner lists into IReadOnlyList<> so callers can't mutate them through the
        // dictionary value type. Cheaper than calling AsReadOnly() per list (which allocates a
        // wrapper); a List<T> already implements IReadOnlyList<T>.
        var frozen = new Dictionary<string, IReadOnlyList<SequenceBinding>>(built.Count, StringComparer.Ordinal);
        foreach (var (k, v) in built) frozen[k] = v;
        _index = frozen;
    }

    /// <summary>Current binding count across all sequences. Diagnostics / settings UI only.</summary>
    public int BindingCount
    {
        get
        {
            var snapshot = _index;
            var count = 0;
            foreach (var v in snapshot.Values) count += v.Count;
            return count;
        }
    }
}
