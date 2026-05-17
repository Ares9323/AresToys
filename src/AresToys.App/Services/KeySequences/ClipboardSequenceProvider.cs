using Microsoft.Extensions.Logging;
using AresToys.Storage.Items;

namespace AresToys.App.Services.KeySequences;

/// <summary>
/// Sources <see cref="ReplaceWithItem"/> bindings from clipboard items whose <c>Trigger</c> field
/// is non-empty. Subscribes to <see cref="IItemStore.ItemsChanged"/> so the binding set updates
/// live when the user adds/edits/deletes triggers via the item editor. Bindings are derived
/// on-demand by <see cref="GetBindings"/> — we don't cache here because the store does the actual
/// work and the matcher already snapshots the final aggregated index.
/// </summary>
public sealed class ClipboardSequenceProvider : ISequenceBindingProvider, IDisposable
{
    private readonly IItemStore _items;
    private readonly ILogger<ClipboardSequenceProvider> _logger;

    public ClipboardSequenceProvider(IItemStore items, ILogger<ClipboardSequenceProvider> logger)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _items.ItemsChanged += OnItemsChanged;
    }

    public event EventHandler? BindingsChanged;

    public IReadOnlyList<SequenceBinding> GetBindings()
    {
        IReadOnlyList<TriggerBinding> rows;
        try
        {
            // Targeted query — only items with a trigger, only (id, trigger) columns. Avoids the
            // full ListAsync path which decrypts payloads + thumbnails for every row.
            rows = _items.ListTriggerBindingsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KS-DEBUG: ClipboardSequenceProvider trigger query FAILED.");
            return Array.Empty<SequenceBinding>();
        }

        var bindings = new List<SequenceBinding>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                bindings.Add(new SequenceBinding(row.Trigger, new ReplaceWithItem(row.Id)));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "KS-DEBUG: dropping item {Id} with invalid trigger '{Trigger}'.", row.Id, row.Trigger);
            }
        }
        _logger.LogInformation("KS-DEBUG: ClipboardSequenceProvider produced {Bindings} bindings (from {Rows} rows with trigger).", bindings.Count, rows.Count);
        return bindings;
    }

    private void OnItemsChanged(object? sender, ItemsChangedEventArgs e)
    {
        _logger.LogInformation("KS-DEBUG: ClipboardSequenceProvider received ItemsChanged kind={Kind} id={Id} → raising BindingsChanged.", e.Kind, e.ItemId);
        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _items.ItemsChanged -= OnItemsChanged;
    }
}
