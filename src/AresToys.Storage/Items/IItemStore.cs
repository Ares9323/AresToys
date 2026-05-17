using AresToys.Core.Domain;

namespace AresToys.Storage.Items;

public interface IItemStore
{
    Task<long> AddAsync(NewItem item, CancellationToken cancellationToken);
    Task<ItemRecord?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ItemRecord>> ListAsync(ItemQuery query, CancellationToken cancellationToken);
    Task<bool> SetPinnedAsync(long id, bool pinned, CancellationToken cancellationToken);
    Task<bool> SetUploadedUrlAsync(long id, string uploaderId, string url, CancellationToken cancellationToken);
    Task<bool> SoftDeleteAsync(long id, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(long id, CancellationToken cancellationToken);
    Task<int> HardDeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
    Task<bool> UpdatePayloadAsync(long id, ReadOnlyMemory<byte> newPayload, long newPayloadSize, CancellationToken cancellationToken);

    /// <summary>Soft-delete every non-pinned item. When <paramref name="category"/> is non-null
    /// the wipe is scoped to that single category bucket; otherwise it spans all categories.
    /// Pinned items are always preserved. Returns the count affected.</summary>
    Task<int> ClearAllExceptPinnedAsync(string? category, CancellationToken cancellationToken);

    /// <summary>Move an item into a different category bucket. Used by the popup's right-click
    /// "Move to → …" menu and by future auto-routing rules. Raises Updated when it changes.</summary>
    Task<bool> SetCategoryAsync(long id, string category, CancellationToken cancellationToken);

    /// <summary>Assign / clear the optional per-item label (CopyQ "Notes" equivalent). Empty or
    /// whitespace-only input is normalised to NULL. Inputs longer than 200 chars are truncated
    /// at the storage boundary so the UI can't smuggle in oversized strings. Raises
    /// <see cref="ItemsChangeKind.Updated"/> when the row was found and updated.</summary>
    Task<bool> SetLabelAsync(long id, string? label, CancellationToken cancellationToken);

    /// <summary>Assign / clear the optional trigger sequence used by the Key Sequences module.
    /// Empty or whitespace-only input is normalised to NULL. Sequence must match [a-zA-Z0-9_]+
    /// (caller validates; store does not enforce because future modules may relax this). Raises
    /// <see cref="ItemsChangeKind.Updated"/> when the row was found and updated.</summary>
    Task<bool> SetTriggerAsync(long id, string? trigger, CancellationToken cancellationToken);

    /// <summary>Cheap projection used by the Key Sequences module to build its matcher index.
    /// Returns only (Id, Trigger) for non-deleted items with a non-empty trigger. Avoids the full
    /// <see cref="ListAsync"/> path which decodes payloads + thumbnails for every row — and which
    /// over-allocates at <c>Limit=int.MaxValue</c> by pre-sizing a List with that capacity.</summary>
    Task<IReadOnlyList<TriggerBinding>> ListTriggerBindingsAsync(CancellationToken cancellationToken);

    /// <summary>Atomically renumber the given pinned items' sort order, in the sequence the
    /// caller passes them (visual top → bottom). Used by drag-reorder and chevron-move on
    /// the pinned strip. Raises a single <see cref="ItemsChangeKind.Updated"/> broadcast so
    /// the popup re-queries once.</summary>
    Task ReorderPinnedAsync(IReadOnlyList<long> orderedIds, CancellationToken cancellationToken);

    /// <summary>Raised after any mutation (add / update / pin / soft-delete / restore). Subscribers
    /// must marshal to the UI thread themselves.</summary>
    event EventHandler<ItemsChangedEventArgs>? ItemsChanged;
}

public sealed record ItemsChangedEventArgs(ItemsChangeKind Kind, long ItemId);

/// <summary>Minimal (Id, Trigger) row returned by <see cref="IItemStore.ListTriggerBindingsAsync"/>.
/// Storage-layer record, no payload / serializer involvement.</summary>
public sealed record TriggerBinding(long Id, string Trigger);

public enum ItemsChangeKind
{
    Added,
    Updated,
    PinnedChanged,
    Deleted,
    Restored
}

public sealed record NewItem(
    ItemKind Kind,
    ItemSource Source,
    DateTimeOffset CreatedAt,
    ReadOnlyMemory<byte> Payload,
    long PayloadSize,
    bool Pinned = false,
    string? SourceProcess = null,
    string? SourceWindow = null,
    string? BlobRef = null,
    string? UploadedUrl = null,
    string? UploaderId = null,
    string? SearchText = null,
    string Category = "Clipboard",
    string? Label = null,
    string? Trigger = null);
