namespace AresToys.Core.Pipeline;

/// <summary>Well-known string keys used in <see cref="PipelineContext.Bag"/> by baked tasks.</summary>
public static class PipelineBagKeys
{
    /// <summary>The <c>NewItem</c> instance to insert via add-to-history. Type: <c>AresToys.Storage.Items.NewItem</c>.</summary>
    public const string NewItem = "new_item";

    /// <summary>Item id assigned after add-to-history. Type: <c>long</c>.</summary>
    public const string ItemId = "item_id";

    /// <summary>Raw payload bytes a task may write to disk. Type: <c>byte[]</c>.</summary>
    public const string PayloadBytes = "payload_bytes";

    /// <summary>File extension to use when save-to-file builds a path (e.g. "png", "txt"). Type: <c>string</c>.</summary>
    public const string FileExtension = "file_extension";

    /// <summary>Absolute path written by save-to-file. Type: <c>string</c>.</summary>
    public const string LocalPath = "local_path";

    /// <summary>First URL produced by an upload task (or the only one when a single uploader was
    /// selected). Type: <c>string</c>.</summary>
    public const string UploadUrl = "upload_url";

    /// <summary>All URLs produced by the upload task, joined by newline. Useful when multiple
    /// uploaders are selected for a category. Type: <c>string</c>.</summary>
    public const string UploadUrls = "upload_urls";

    /// <summary>Id of the (first) uploader that produced <see cref="UploadUrl"/>. Type: <c>string</c>.</summary>
    public const string UploaderId = "uploader_id";

    /// <summary>Title of the window that was snap-captured (when applicable). Type: <c>string</c>.</summary>
    public const string WindowTitle = "window_title";

    /// <summary>Top-left of the captured region in physical screen pixels — set by the region/
    /// monitor/fullscreen capture tasks so downstream tasks (notably <c>arestoys.pin-to-screen</c>)
    /// can reproduce the exact on-screen origin. Type: <c>(int X, int Y)</c>.</summary>
    public const string CaptureScreenPos = "capture_screen_pos";

    /// <summary>Colour produced by the color-sampler / color-picker steps. Downstream
    /// <c>arestoys.copy-color-*</c> tasks read this and emit the colour in their respective format
    /// to the clipboard, letting users compose "pick → format" pipelines without per-format
    /// hardcoding inside the sampler. Type: <c>AresToys.Editor.Model.ShapeColor</c>.</summary>
    public const string Color = "color";

    /// <summary>Set to <c>true</c> by <c>OpenEditorBeforeUploadTask</c> when the user saved a
    /// genuinely-different version of the bytes (post-edit payload differs from pre-edit).
    /// Downstream tasks with a <c>skipIfNotModified</c> toggle read this to know whether to
    /// run — lets workflows build "save before/after only when actually edited" flows.</summary>
    public const string PayloadModified = "payload_modified";

    /// <summary>The pipeline's "current text" — overwritten by every step that produces a
    /// textual artifact (SaveToFile / SaveSvg / SaveAs / RecordScreen → the saved path; Upload →
    /// the first URL; QrRead → the decoded text). Read as a zero-config default by AddText,
    /// AddFile, the QR converters and the ToastBuilder. Workflows are linear, so "last writer
    /// wins" matches the user's mental model — Add text after Upload gets the URL, Add text
    /// after Scan QR gets the decoded text, etc.</summary>
    public const string Text = "text";
}
