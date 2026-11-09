namespace AresToys.Clipboard;

/// <summary>Describes a clipboard event after format detection but before persistence.</summary>
public sealed record ClipboardChange(
    ClipboardFormat Format,
    DateTimeOffset CapturedAt,
    string? SourceProcess,
    string? SourceWindow,
    ReadOnlyMemory<byte> Payload,
    string? PreviewText,
    string[]? FilePaths);
