using AresToys.Core.Domain;

namespace AresToys.Storage.Items;

public interface IItemStore
{
    Task<long> AddAsync(NewItem item, CancellationToken cancellationToken);
    Task<ItemRecord?> GetByIdAsync(long id, CancellationToken cancellationToken);
    /// <summary>Same row as <see cref="GetByIdAsync(long, CancellationToken)"/>; with
    /// <paramref name="includePayload"/> = false the payload column is skipped entirely (empty
    /// <see cref="ItemRecord.Payload"/>), so no DPAPI decryption happens. Use it whenever only
    /// metadata / BlobRef is needed (video preview, file-drop publish).</summary>
    Task<ItemRecord?> GetByIdAsync(long id, bool includePayload, CancellationToken cancellationToken);
    Task<IReadOnlyList<ItemRecord>> ListAsync(ItemQuery query, CancellationToken cancellationToken);
    Task<bool> SetPinnedAsync(long id, bool pinned, CancellationToken cancellationToken);
    Task<bool> SetUploadedUrlAsync(long id, string uploaderId, string url, CancellationToken cancellationToken);
    Task<bool> SoftDeleteAsync(long id, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(long id, CancellationToken cancellationToken);
    Task<int> HardDeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
    /// <summary>Replace the payload of an existing item, optionally flipping its
    /// <see cref="ItemKind"/> at the same time. <paramref name="newKind"/> = null leaves the
    /// kind as-is (typical external-editor save-back where bytes change but the format doesn't).
    /// Non-null flips it (e.g. "Convert HTML → plain text" turns an <c>Html</c> item into a
    /// <c>Text</c> item so the popup renders + pastes it as plain text afterwards). Single SQL
    /// roundtrip; raises one <see cref="ItemsChangeKind.Updated"/> broadcast.</summary>
    Task<bool> UpdatePayloadAsync(long id, ReadOnlyMemory<byte> newPayload, long newPayloadSize, ItemKind? newKind, CancellationToken cancellationToken);

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

    /// <summary>Attach a tag to an item (issue #4). The single entry point for every tag
    /// producer: the popup's "Add tag" menu today, automatic tagging by content type or rules
    /// later (issue #19). Idempotent; returns true only when the link was actually added, and
    /// false when it already existed or the item / tag doesn't exist. Does NOT enforce
    /// <see cref="TagRules.IsTaggable"/>: whether an item should receive tags is the caller's
    /// policy. Re-indexes the item for search and raises <see cref="ItemsChangeKind.Updated"/>.</summary>
    Task<bool> AddTagAsync(long itemId, long tagId, CancellationToken cancellationToken);

    /// <summary>Detach a tag from an item. Returns true when a link was removed. Re-indexes the
    /// item for search and raises <see cref="ItemsChangeKind.Updated"/>.</summary>
    Task<bool> RemoveTagAsync(long itemId, long tagId, CancellationToken cancellationToken);

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

    /// <summary>Store a row thumbnail generated after the item was added (the clipboard window
    /// builds one lazily for Files rows that point at an image). Only fills an empty column, so it
    /// never overwrites a thumbnail generated at ingestion. Deliberately raises no
    /// <see cref="ItemsChanged"/> broadcast: the caller already updated the visible row, and a
    /// refresh per thumbnail would rebuild the whole list for nothing.</summary>
    Task<bool> SetThumbnailIfMissingAsync(long id, byte[] thumbnail, CancellationToken cancellationToken);

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
    string? Trigger = null,
    // Row thumbnail (PNG) prepared by the caller. Image items get one generated from their
    // payload when this is null; Files items pointing at an image file pass one here because
    // only the app layer reads and decodes the file (WebP needs Skia to keep its alpha).
    byte[]? Thumbnail = null);
