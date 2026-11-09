using System.IO;

namespace AresToys.App.Services.Peek;

/// <summary>Which files of the folder Peek's previous and next go through.</summary>
public enum PeekBrowseMode
{
    /// <summary>Every file of the folder.</summary>
    All,

    /// <summary>Only files of the same family as the current one (images, videos, 3D, ...).</summary>
    Family,

    /// <summary>Only files with the same extension as the current one.</summary>
    Extension,
}

/// <summary>The browse filter: the families, the filter function and the text of the saved value. The value itself is
/// saved by <see cref="PeekWindowStore"/> under <see cref="PeekWindowStore.BrowseKey"/>.</summary>
internal static class PeekBrowse
{
    private static readonly HashSet<string> ThreeD = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".3mf", ".obj", ".ply", ".gltf", ".glb", ".blend",
    };

    private static readonly HashSet<string> Vector = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".ai", ".vectorcraft",
    };

    private static readonly HashSet<string> Font = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".ttc", ".otc",
    };

    private static readonly HashSet<string> Document = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf",
    };

    /// <summary>The text stored for <paramref name="mode"/>.</summary>
    public static string Format(PeekBrowseMode mode) => mode switch
    {
        PeekBrowseMode.Family => "family",
        PeekBrowseMode.Extension => "extension",
        _ => "all",
    };

    /// <summary>The mode stored as <paramref name="raw"/>; <see cref="PeekBrowseMode.All"/> for anything unknown.</summary>
    public static PeekBrowseMode Parse(string? raw) => raw switch
    {
        "family" => PeekBrowseMode.Family,
        "extension" => PeekBrowseMode.Extension,
        _ => PeekBrowseMode.All,
    };

    public static string DisplayName(PeekBrowseMode mode) => mode switch
    {
        PeekBrowseMode.Family => AresToys.App.Resources.Strings.Peek_NavFamily,
        PeekBrowseMode.Extension => AresToys.App.Resources.Strings.Peek_NavExtension,
        _ => AresToys.App.Resources.Strings.Peek_NavAll,
    };

    /// <summary>The files of <paramref name="files"/> that <paramref name="mode"/> lets Peek walk, in their order, and
    /// the position of <paramref name="current"/> in them. The current file always stays in the list.</summary>
    public static (IReadOnlyList<string> Files, int Index) Filter(IReadOnlyList<string> files, string? current, PeekBrowseMode mode)
    {
        var currentIndex = current is null ? -1 : IndexOf(files, current);
        if (mode == PeekBrowseMode.All || current is null || currentIndex < 0)
        {
            return (files, Math.Max(currentIndex, 0));
        }

        var kept = new List<string>(files.Count);
        var keptIndex = 0;
        if (mode == PeekBrowseMode.Extension)
        {
            var extension = Path.GetExtension(current);
            for (var i = 0; i < files.Count; i++)
            {
                if (i == currentIndex) keptIndex = kept.Count;
                if (i == currentIndex || string.Equals(Path.GetExtension(files[i]), extension, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(files[i]);
                }
            }

            return (kept, keptIndex);
        }

        // One family lookup per extension: an unknown extension costs a preview handler lookup and a text sniff.
        var familyByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string FamilyCached(string path)
        {
            var extension = Path.GetExtension(path);
            if (!familyByExtension.TryGetValue(extension, out var family))
            {
                family = FamilyOf(path);
                familyByExtension[extension] = family;
            }

            return family;
        }

        var currentFamily = FamilyCached(current);
        for (var i = 0; i < files.Count; i++)
        {
            if (i == currentIndex) keptIndex = kept.Count;
            if (i == currentIndex || string.Equals(FamilyCached(files[i]), currentFamily, StringComparison.Ordinal))
            {
                kept.Add(files[i]);
            }
        }

        return (kept, keptIndex);
    }

    /// <summary>The family of a file: a name shared by the extensions that belong together. A file in no family is its
    /// own family (its extension), so Same family behaves like Same extension for it.</summary>
    internal static string FamilyOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (ThreeD.Contains(extension)) return "3d";
        if (Vector.Contains(extension)) return "vector";
        if (Font.Contains(extension)) return "font";
        if (Document.Contains(extension)) return "document";
        return PeekPreview.AutoKindFor(path) switch
        {
            PeekPreviewKind.Image => "image",
            PeekPreviewKind.Video => "video",
            PeekPreviewKind.Code or PeekPreviewKind.Markdown or PeekPreviewKind.Html => "text",
            _ => "ext:" + extension.ToLowerInvariant(),
        };
    }

    private static int IndexOf(IReadOnlyList<string> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (string.Equals(files[i], path, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}
