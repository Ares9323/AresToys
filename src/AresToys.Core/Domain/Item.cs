namespace AresToys.Core.Domain;

public sealed record Item(
    long Id,
    ItemKind Kind,
    ItemSource Source,
    DateTimeOffset CreatedAt,
    long PayloadSize,
    bool Pinned = false,
    DateTimeOffset? DeletedAt = null,
    string? SourceProcess = null,
    string? SourceWindow = null,
    string? BlobRef = null,
    string? UploadedUrl = null,
    string? UploaderId = null,
    /// <summary>User-defined category bucket (CopyQ-style "tabs"). Defaults to the built-in
    /// "Clipboard" category which receives every item that's copied without an explicit
    /// override. The user can move items between categories from the popup context menu.</summary>
    string Category = "Clipboard");
