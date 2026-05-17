using AresToys.Core.Domain;

namespace AresToys.Storage.Items;

/// <summary>
/// Storage-layer view of an item. Identity (Id, Kind, Source, CreatedAt) matches the domain
/// <see cref="Item"/>; storage-specific fields (Payload, SearchText) live only here.
/// </summary>
public sealed record ItemRecord(
    long Id,
    ItemKind Kind,
    ItemSource Source,
    DateTimeOffset CreatedAt,
    long PayloadSize,
    bool Pinned,
    DateTimeOffset? DeletedAt,
    string? SourceProcess,
    string? SourceWindow,
    string? BlobRef,
    string? UploadedUrl,
    string? UploaderId,
    ReadOnlyMemory<byte> Payload,
    string? SearchText,
    ReadOnlyMemory<byte>? Thumbnail = null,
    string Category = "Clipboard",
    string? Label = null,
    int PinSortOrder = 0,
    string? Trigger = null)
{
    public Item ToDomain() => new(
        Id: Id,
        Kind: Kind,
        Source: Source,
        CreatedAt: CreatedAt,
        PayloadSize: PayloadSize,
        Pinned: Pinned,
        DeletedAt: DeletedAt,
        SourceProcess: SourceProcess,
        SourceWindow: SourceWindow,
        BlobRef: BlobRef,
        UploadedUrl: UploadedUrl,
        UploaderId: UploaderId,
        Category: Category);
}
