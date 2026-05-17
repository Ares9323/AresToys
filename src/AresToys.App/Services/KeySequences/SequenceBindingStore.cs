using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.KeySequences;

/// <summary>
/// Aggregates bindings from all registered <see cref="ISequenceBindingProvider"/> instances and
/// keeps the <see cref="SequenceMatcher"/> index in sync. Each provider raises
/// <see cref="ISequenceBindingProvider.BindingsChanged"/> when its source mutates; the store
/// re-aggregates and pushes the new set into the matcher.
///
/// Conflict policy at aggregation time (matches design spec):
/// - Multiple <see cref="ReplaceWithItem"/> bindings sharing a sequence → kept (overlay lists all).
/// - Multiple <see cref="RunWorkflow"/> bindings on the same sequence → first wins, others dropped + warning.
/// - <see cref="ReplaceWithItem"/> + <see cref="RunWorkflow"/> on the same sequence → Replacer wins
///   (soft path with overlay confirmation), Workflow dropped + warning. UI prevents this state but
///   we degrade gracefully if it slips through.
/// </summary>
public sealed class SequenceBindingStore : IDisposable
{
    private readonly IReadOnlyList<ISequenceBindingProvider> _providers;
    private readonly SequenceMatcher _matcher;
    private readonly ILogger<SequenceBindingStore> _logger;
    private readonly object _rebuildLock = new();

    public SequenceBindingStore(
        IEnumerable<ISequenceBindingProvider> providers,
        SequenceMatcher matcher,
        ILogger<SequenceBindingStore> logger)
    {
        _providers = providers?.ToList() ?? throw new ArgumentNullException(nameof(providers));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        foreach (var p in _providers) p.BindingsChanged += OnProviderChanged;
        _logger.LogInformation("KS-DEBUG: SequenceBindingStore ctor — subscribed to {N} providers: [{Types}]", _providers.Count, string.Join(", ", _providers.Select(p => p.GetType().Name)));
    }

    /// <summary>Raised after the matcher has been rebuilt with a fresh aggregate. Subscribers like
    /// the settings UI can refresh diagnostic counts on this.</summary>
    public event EventHandler? AggregateChanged;

    /// <summary>Force a rebuild now. Called once at startup and on every provider change.</summary>
    public void Rebuild()
    {
        int total;
        lock (_rebuildLock)
        {
            var aggregated = AggregateWithConflictResolution();
            total = aggregated.Count;
            _matcher.Rebuild(aggregated);
        }
        _logger.LogInformation("KS-DEBUG: SequenceBindingStore.Rebuild aggregated {Total} bindings into matcher.", total);
        AggregateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnProviderChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("KS-DEBUG: SequenceBindingStore received BindingsChanged from provider {Type} → triggering Rebuild.", sender?.GetType().Name ?? "?");
        Rebuild();
    }

    private List<SequenceBinding> AggregateWithConflictResolution()
    {
        // First pass: bucket by sequence so we can resolve conflicts predictably regardless of
        // provider ordering. Replacer bindings keep their multi-binding semantics; Workflow
        // bindings collapse to at most one per sequence.
        var bySequence = new Dictionary<string, (List<SequenceBinding> replacers, SequenceBinding? workflow)>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            foreach (var binding in provider.GetBindings())
            {
                if (!bySequence.TryGetValue(binding.Sequence, out var bucket))
                {
                    bucket = ([], null);
                    bySequence[binding.Sequence] = bucket;
                }
                switch (binding.Target)
                {
                    case ReplaceWithItem:
                        bucket.replacers.Add(binding);
                        bySequence[binding.Sequence] = bucket;
                        break;
                    case RunWorkflow:
                        if (bucket.workflow is null)
                        {
                            bucket = (bucket.replacers, binding);
                            bySequence[binding.Sequence] = bucket;
                        }
                        else
                        {
                            _logger.LogWarning("KeySequences: duplicate workflow binding for sequence '{Seq}' — keeping first, dropping subsequent.", binding.Sequence);
                        }
                        break;
                }
            }
        }

        var result = new List<SequenceBinding>();
        foreach (var (seq, bucket) in bySequence)
        {
            if (bucket.replacers.Count > 0 && bucket.workflow is not null)
            {
                _logger.LogWarning("KeySequences: sequence '{Seq}' has both Replacer and Workflow bindings — Replacer wins, Workflow dropped (UI should prevent this).", seq);
                result.AddRange(bucket.replacers);
            }
            else if (bucket.replacers.Count > 0)
            {
                result.AddRange(bucket.replacers);
            }
            else if (bucket.workflow is not null)
            {
                result.Add(bucket.workflow);
            }
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var p in _providers) p.BindingsChanged -= OnProviderChanged;
    }
}
