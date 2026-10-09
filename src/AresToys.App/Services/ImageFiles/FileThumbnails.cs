using System.IO;

namespace AresToys.App.Services.ImageFiles;

/// <summary>Row thumbnails for Files items (paths copied from Explorer). The thumbnail comes
/// from the first image among the paths and matches the one Image items get at ingestion:
/// PNG, longer side <see cref="MaxSide"/>. The file is read into memory and closed right away
/// (never kept locked) and decoded straight at thumbnail size.</summary>
public static class FileThumbnails
{
    /// <summary>Same cap ItemStore uses for Image thumbnails.</summary>
    public const int MaxSide = 96;

    /// <summary>Files bigger than this are skipped: reading them only to throw away all but a
    /// 96 px thumbnail is not worth the transient memory.</summary>
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    /// <summary>Extensions decoded for a thumbnail. GIF included: the first frame is enough here,
    /// even though the preview pane plays animated GIFs as video.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico",
    };

    public static bool IsImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    /// <summary>Split a Files item's path list (newline joined, LF or CRLF) into trimmed,
    /// non-empty entries.</summary>
    public static IReadOnlyList<string> ParsePaths(string? pathList)
        => string.IsNullOrWhiteSpace(pathList)
            ? []
            : pathList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>First path whose extension is an image type, or null.</summary>
    public static string? FirstImagePath(IEnumerable<string> paths)
        => paths.FirstOrDefault(IsImagePath);

    /// <summary>Thumbnail of the first image among <paramref name="paths"/> that exists and
    /// decodes. Null when there is none: the row then just shows its text.</summary>
    public static byte[]? TryGenerate(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (!IsImagePath(path)) continue;
            var thumb = TryGenerateFromFile(path);
            if (thumb is not null) return thumb;
        }
        return null;
    }

    /// <summary>Thumbnail of one image file; null when it is missing, too big, unreadable or not
    /// a decodable image.</summary>
    public static byte[]? TryGenerateFromFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSourceBytes) return null;
            // ReadAllBytes opens with FileShare.Read and closes before decoding starts.
            var bytes = File.ReadAllBytes(path);
            return TryGenerateFromBytes(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static byte[]? TryGenerateFromBytes(byte[] bytes)
    {
        var bitmap = ImageDecoding.Decode(bytes, MaxSide);
        if (bitmap is null) return null;
        try { return ImageDecoding.EncodePng(bitmap); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
