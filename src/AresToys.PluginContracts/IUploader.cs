namespace AresToys.PluginContracts;

public interface IUploader
{
    /// <summary>Stable id used in pipeline configs and settings (e.g. "catbox").</summary>
    string Id { get; }

    /// <summary>Human-readable name for settings UI.</summary>
    string DisplayName { get; }

    /// <summary>What kinds of content this uploader supports. Defaults to <see cref="UploaderCapabilities.AnyFile"/>
    /// so generic file hosts (Catbox, OneDrive, S3, ...) only need to override when restricting
    /// (e.g. Imgur → <c>UploaderCapabilities.Image</c>; Pastebin → <c>UploaderCapabilities.Text</c>).</summary>
    UploaderCapabilities Capabilities => UploaderCapabilities.AnyFile;

    Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken);
}

public sealed record UploadRequest(byte[] Bytes, string FileName, string ContentType);

public sealed record UploadResult(bool Ok, string? Url, string? ErrorMessage)
{
    public static UploadResult Success(string url) => new(true, url, null);
    public static UploadResult Failure(string message) => new(false, null, message);
}
