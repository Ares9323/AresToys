using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.KeySequences;

/// <summary>
/// Sources <see cref="RunWorkflow"/> bindings from a JSON list persisted in
/// <see cref="ISettingsStore"/> under the key <see cref="StorageKey"/>. The settings UI edits this
/// list via <see cref="ReplaceAsync"/> (full-replace, simpler than per-row CRUD because the list
/// is short by nature — workflow triggers are a power-user feature with usually &lt;20 entries).
/// </summary>
public sealed class WorkflowSequenceProvider : ISequenceBindingProvider
{
    public const string StorageKey = "keysequences.workflow-triggers";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ISettingsStore _settings;
    private readonly ILogger<WorkflowSequenceProvider> _logger;
    private readonly object _cacheLock = new();
    private IReadOnlyList<WorkflowTriggerEntry> _cache = Array.Empty<WorkflowTriggerEntry>();

    public WorkflowSequenceProvider(ISettingsStore settings, ILogger<WorkflowSequenceProvider> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler? BindingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var json = await _settings.GetAsync(StorageKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            lock (_cacheLock) _cache = Array.Empty<WorkflowTriggerEntry>();
            return;
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<WorkflowTriggerEntry>>(json, JsonOpts)
                       ?? new List<WorkflowTriggerEntry>();
            lock (_cacheLock) _cache = list;
        }
        catch (JsonException ex)
        {
            // Corrupt settings shouldn't take the whole module down — log, fall back to empty,
            // and let the user re-create entries in the UI. The corrupted JSON is preserved in
            // the settings store; if we overwrote it we'd lose data the user might recover.
            _logger.LogError(ex, "WorkflowSequenceProvider: failed to parse {Key}; using empty list.", StorageKey);
            lock (_cacheLock) _cache = Array.Empty<WorkflowTriggerEntry>();
        }
    }

    public IReadOnlyList<WorkflowTriggerEntry> Snapshot()
    {
        lock (_cacheLock) return _cache.ToList();
    }

    public async Task ReplaceAsync(IReadOnlyList<WorkflowTriggerEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // Defensive copy + validate before persisting so we never write a partially-rejected list.
        var validated = new List<WorkflowTriggerEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Sequence)) throw new ArgumentException("Sequence must be non-empty.");
            if (string.IsNullOrWhiteSpace(e.WorkflowId)) throw new ArgumentException("WorkflowId must be non-empty.");
            validated.Add(e);
        }
        var json = JsonSerializer.Serialize(validated, JsonOpts);
        await _settings.SetAsync(StorageKey, json, sensitive: false, cancellationToken).ConfigureAwait(false);
        lock (_cacheLock) _cache = validated;
        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<SequenceBinding> GetBindings()
    {
        IReadOnlyList<WorkflowTriggerEntry> snapshot;
        lock (_cacheLock) snapshot = _cache;

        var bindings = new List<SequenceBinding>(snapshot.Count);
        foreach (var e in snapshot)
        {
            try
            {
                bindings.Add(new SequenceBinding(e.Sequence, new RunWorkflow(e.WorkflowId)));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "WorkflowSequenceProvider: dropping invalid entry sequence='{Seq}' workflowId='{Wf}'.", e.Sequence, e.WorkflowId);
            }
        }
        return bindings;
    }
}

/// <summary>Persistence shape for one workflow trigger row. <paramref name="Label"/> is a
/// denormalized cache of the workflow's display name so the settings list renders without a
/// per-row workflow lookup; the settings UI refreshes it whenever the underlying workflow is
/// renamed.</summary>
public sealed record WorkflowTriggerEntry(
    [property: JsonPropertyName("sequence")] string Sequence,
    [property: JsonPropertyName("workflowId")] string WorkflowId,
    [property: JsonPropertyName("label")] string? Label = null);
