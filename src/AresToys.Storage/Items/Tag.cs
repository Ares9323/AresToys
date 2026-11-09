using System.Text.RegularExpressions;

namespace AresToys.Storage.Items;

/// <summary>One clipboard tag definition (issue #4). Tags are many-to-many with items and
/// orthogonal to categories: a category is where an item lives, tags are what it is about.
/// <see cref="Color"/> is <c>#RRGGBB</c> or null for the neutral default chip.
/// <see cref="ItemCount"/> is filled by <see cref="ITagStore.ListAsync"/> (non-deleted items
/// carrying the tag) and is 0 on rows returned by the single-tag lookups.</summary>
public sealed record Tag(long Id, string Name, string? Color, int ItemCount = 0);

/// <summary>Pure rules shared by the storage layer, the UI and future automatic taggers
/// (issue #19), so every producer normalises names / colours the same way and agrees on which
/// items can receive tags.</summary>
public static partial class TagRules
{
    public const int MaxNameLength = 40;

    /// <summary>An item can receive new tags only when nothing will remove it on its own:
    /// pinned items are exempt from every retention sweep (CategoryRotationService skips
    /// <c>pinned = 1</c>), and an unpinned item is safe only in a category with neither a
    /// max-count cap nor a time-based cleanup. A null category (the item points at a bucket
    /// that has no row) has no caps either. Tags already on an item that later becomes
    /// non-taggable are kept: they're harmless and vanish with the item if it gets purged.</summary>
    public static bool IsTaggable(bool pinned, Category? category)
        => pinned || category is null || (category.MaxItems <= 0 && category.AutoCleanupAfter <= 0);

    /// <summary>Trim, collapse inner whitespace runs to one space, cap at
    /// <see cref="MaxNameLength"/>. Returns null when nothing is left.</summary>
    public static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var collapsed = WhitespaceRun().Replace(raw.Trim(), " ");
        return collapsed.Length <= MaxNameLength ? collapsed : collapsed[..MaxNameLength].TrimEnd();
    }

    /// <summary>Accepts <c>#RRGGBB</c> (with or without the hash, any case) and returns it as
    /// upper-case <c>#RRGGBB</c>. Anything else, empty included, means "no colour" (null).</summary>
    public static string? NormalizeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith('#')) s = s[1..];
        return HexColor().IsMatch(s) ? "#" + s.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex("^[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
