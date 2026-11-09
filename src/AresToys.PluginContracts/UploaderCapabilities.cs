namespace AresToys.PluginContracts;

/// <summary>
/// Content types an uploader supports. The host's settings UI groups uploaders by capability so
/// the user can pick separately the destination for screenshots, file shares, and text snippets.
/// Mirrors the ShareX split between image / file / text uploaders.
/// </summary>
[Flags]
public enum UploaderCapabilities
{
    None  = 0,
    Image = 1 << 0,
    File  = 1 << 1,
    Text  = 1 << 2,
    Video = 1 << 3,
    /// <summary>URL shortener — input is a URL (text), output is a shorter URL. Kept separate
    /// from <see cref="Text"/> so an arbitrary text upload doesn't accidentally fan out to a
    /// shortener that would mangle the content into a meaningless redirect link.</summary>
    Url   = 1 << 4,

    /// <summary>Generic file host — accepts any binary including images, video, and text.
    /// Excludes <see cref="Url"/> on purpose: a generic file host can hold a .txt with a URL
    /// in it, but that's not the same as turning the URL itself into a shorter URL.</summary>
    AnyFile = Image | File | Text | Video,
}
