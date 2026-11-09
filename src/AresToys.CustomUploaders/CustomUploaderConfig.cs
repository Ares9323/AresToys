using System.Text.Json.Serialization;

namespace AresToys.CustomUploaders;

/// <summary>Declarative description of an HTTP-based upload destination, parsed from a ShareX
/// <c>.sxcu</c> file. Field names + casing mirror the ShareX schema verbatim so off-the-shelf
/// JSON files from <c>getsharex.com/custom-uploader</c> import without conversion.</summary>
/// <remarks>
/// What we support today:
/// <list type="bullet">
/// <item><description><see cref="RequestMethod"/>: POST / GET / PUT / PATCH / DELETE.</description></item>
/// <item><description><see cref="Body"/>: <c>MultipartFormData</c> (the file goes in <see cref="FileFormName"/>),
///     <c>FormURLEncoded</c>, <c>JSON</c> / <c>XML</c> (raw <see cref="Data"/>), <c>Binary</c>
///     (raw bytes), <c>None</c>.</description></item>
/// <item><description>Templating in <see cref="RequestURL"/> / <see cref="Parameters"/> /
///     <see cref="Headers"/> / <see cref="Arguments"/> / <see cref="Data"/> /
///     <see cref="URL"/> / <see cref="ErrorMessage"/>.</description></item>
/// </list>
/// What we DON'T support yet (ignored when present): interactive <c>$prompt:label$</c>
/// substitutions, <c>$random:abc$</c> / <c>$rndhex:N$</c> generators, <c>{xml:xpath}</c> response
/// parsing, custom thumbnail / deletion URL plumbing into the pipeline. <see cref="ThumbnailURL"/>
/// and <see cref="DeletionURL"/> are still parsed so the JSON round-trips losslessly, just not yet
/// surfaced to the user.
/// </remarks>
public sealed record CustomUploaderConfig
{
    [JsonPropertyName("Version")]    public string? Version { get; init; }
    [JsonPropertyName("Name")]       public string? Name { get; init; }

    /// <summary>Maps to <see cref="AresToys.PluginContracts.UploaderCapabilities"/>: ImageUploader →
    /// Image, TextUploader → Text, FileUploader → File, URLShortener / URLSharingService → Text
    /// (treated as text for routing). <c>null</c> / <c>None</c> falls back to AnyFile.</summary>
    [JsonPropertyName("DestinationType")] public string? DestinationType { get; init; }

    [JsonPropertyName("RequestMethod")] public string? RequestMethod { get; init; }
    [JsonPropertyName("RequestURL")]    public string? RequestURL { get; init; }

    /// <summary>Query-string parameters appended to <see cref="RequestURL"/>. Values are templated.</summary>
    [JsonPropertyName("Parameters")] public Dictionary<string, string>? Parameters { get; init; }

    /// <summary>Custom HTTP request headers. Values are templated.</summary>
    [JsonPropertyName("Headers")] public Dictionary<string, string>? Headers { get; init; }

    /// <summary><c>MultipartFormData</c> | <c>FormURLEncoded</c> | <c>JSON</c> | <c>XML</c> |
    /// <c>Binary</c> | <c>None</c>. Default is multipart.</summary>
    [JsonPropertyName("Body")] public string? Body { get; init; }

    /// <summary>Form-field name for the file payload when <see cref="Body"/> is multipart. ShareX
    /// defaults to <c>"file"</c> when missing — we follow that.</summary>
    [JsonPropertyName("FileFormName")] public string? FileFormName { get; init; }

    /// <summary>Per-field arguments included in the body (multipart text parts or url-encoded
    /// form pairs). Values are templated.</summary>
    [JsonPropertyName("Arguments")] public Dictionary<string, string>? Arguments { get; init; }

    /// <summary>Raw body string for JSON / XML / None bodies. Templated end-to-end.</summary>
    [JsonPropertyName("Data")] public string? Data { get; init; }

    /// <summary>Template for the URL that becomes the upload result. Common shapes:
    /// <c>{json:url}</c>, <c>{json:data.link}</c>, <c>{response}</c>, or a literal pattern like
    /// <c>https://example.com/{json:id}</c>.</summary>
    [JsonPropertyName("URL")] public string? URL { get; init; }

    [JsonPropertyName("ThumbnailURL")] public string? ThumbnailURL { get; init; }
    [JsonPropertyName("DeletionURL")]  public string? DeletionURL { get; init; }

    /// <summary>Template applied to non-2xx responses (or 2xx with malformed payload) to produce
    /// a user-readable failure message. Falls back to the raw body when missing.</summary>
    [JsonPropertyName("ErrorMessage")] public string? ErrorMessage { get; init; }
}
