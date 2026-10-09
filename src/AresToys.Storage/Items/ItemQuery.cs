using AresToys.Core.Domain;

namespace AresToys.Storage.Items;

public sealed record ItemQuery(
    int Limit = 100,
    int Offset = 0,
    ItemKind? Kind = null,
    bool? Pinned = null,
    bool IncludeDeleted = false,
    string? Search = null,
    bool IncludePayload = true,
    bool IncludeThumbnail = true,
    /// <summary>When set, restrict results to items in this category. null = all categories
    /// (the popup's "All" tab). Empty string is treated like null.</summary>
    string? Category = null,
    /// <summary>When non-empty, restrict results to items carrying these tags: EVERY one of them
    /// by default (AND), or at least one when <see cref="TagMatchAny"/> is set (OR).
    /// null / empty = no tag filter.</summary>
    IReadOnlyList<long>? TagIds = null,
    /// <summary>OR semantics for <see cref="TagIds"/>: an item matches when it carries any of
    /// the requested tags. Ignored when there is no tag filter.</summary>
    bool TagMatchAny = false);
