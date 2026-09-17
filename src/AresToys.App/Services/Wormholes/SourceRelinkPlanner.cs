using System.IO;

namespace AresToys.App.Services.Wormholes;

/// <summary>One proposed repair: <paramref name="Id"/>'s source should move from
/// <paramref name="OldPath"/> to <paramref name="NewPath"/>.</summary>
public sealed record SourceRelinkProposal(Guid Id, string OldPath, string NewPath);

/// <summary>Works out where a batch of missing wormhole sources went, by folder NAME rather than
/// by identity. This is the fallback for everything <see cref="FolderIdentityResolver"/> can't
/// do: a folder moved to a different volume (new file id), restored from a backup, or recreated
/// by hand.
///
/// Pure and side-effect free — existence checks come in through a delegate — so the rules can be
/// unit-tested without touching a disk.</summary>
public static class SourceRelinkPlanner
{
    /// <summary>Re-root every missing path under <paramref name="newBase"/>, keeping whatever
    /// structure the paths had below their common parent. Proposals are only emitted for targets
    /// that actually exist, so a wrong folder pick simply yields nothing instead of writing
    /// garbage into the records.
    ///
    /// Two shapes are tried per entry, most specific first:
    /// <list type="number">
    /// <item><description><c>newBase\&lt;path below the common parent&gt;</c> — the "I renamed
    /// the folder that held all my wormholes" case.</description></item>
    /// <item><description><c>newBase\&lt;leaf folder name&gt;</c> — covers a flatter
    /// reorganisation where only the leaf survived.</description></item>
    /// </list></summary>
    public static IReadOnlyList<SourceRelinkProposal> Plan(
        IReadOnlyList<(Guid Id, string OldPath)> missing,
        string newBase,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (string.IsNullOrWhiteSpace(newBase) || missing.Count == 0) return [];

        var commonParent = CommonParent(missing.Select(m => m.OldPath).ToList());
        var proposals = new List<SourceRelinkProposal>();

        foreach (var (id, oldPath) in missing)
        {
            if (string.IsNullOrWhiteSpace(oldPath)) continue;
            var candidate = FirstExisting(Candidates(oldPath, commonParent, newBase), directoryExists);
            if (candidate is not null && !PathsEqual(candidate, oldPath))
                proposals.Add(new SourceRelinkProposal(id, oldPath, candidate));
        }
        return proposals;
    }

    /// <summary>Best guess for a SINGLE missing path, given the sources that are still healthy.
    /// Used by a wormhole's "source folder unavailable" panel: if its siblings now live under a
    /// different parent, this one probably belongs there too.</summary>
    public static string? Suggest(
        string missingPath,
        IReadOnlyList<string> healthyPaths,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(healthyPaths);
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (string.IsNullOrWhiteSpace(missingPath) || healthyPaths.Count == 0) return null;

        var leaf = LeafName(missingPath);
        if (leaf.Length == 0) return null;

        // Every distinct parent the healthy sources sit in is a plausible new home; prefer the
        // one shared by most of them.
        var parents = healthyPaths
            .Select(ParentOf)
            .Where(p => !string.IsNullOrEmpty(p))
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key);

        foreach (var parent in parents)
        {
            var candidate = Path.Combine(parent, leaf);
            if (!PathsEqual(candidate, missingPath) && directoryExists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string oldPath, string? commonParent, string newBase)
    {
        if (!string.IsNullOrEmpty(commonParent) && StartsUnder(oldPath, commonParent))
        {
            var relative = oldPath[commonParent.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length > 0) yield return Path.Combine(newBase, relative);
        }
        var leaf = LeafName(oldPath);
        if (leaf.Length > 0) yield return Path.Combine(newBase, leaf);
    }

    private static string? FirstExisting(IEnumerable<string> candidates, Func<string, bool> exists)
    {
        foreach (var candidate in candidates)
        {
            if (exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Deepest directory every path sits under, or null when they share nothing (paths
    /// spread across several roots). Public so the rule can be pinned by tests — it decides how
    /// much of the old structure a bulk relink tries to preserve.</summary>
    public static string? CommonParent(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        string? common = null;
        foreach (var path in paths)
        {
            var parent = ParentOf(path);
            if (string.IsNullOrEmpty(parent)) return null;
            common = common is null ? parent : LongestSharedPrefix(common, parent);
            if (string.IsNullOrEmpty(common)) return null;
        }
        return common;
    }

    private static string LongestSharedPrefix(string a, string b)
    {
        var left = Split(a);
        var right = Split(b);
        var shared = new List<string>();
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase)) break;
            shared.Add(left[i]);
        }
        if (shared.Count == 0) return string.Empty;
        var joined = string.Join(Path.DirectorySeparatorChar, shared);
        // A bare drive letter ("C:") needs its separator back to be a usable path.
        return joined.EndsWith(':') ? joined + Path.DirectorySeparatorChar : joined;
    }

    private static string[] Split(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static bool StartsUnder(string path, string parent)
    {
        var normalisedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Length > normalisedParent.Length
            && path.StartsWith(normalisedParent, StringComparison.OrdinalIgnoreCase)
            && (path[normalisedParent.Length] == Path.DirectorySeparatorChar
                || path[normalisedParent.Length] == Path.AltDirectorySeparatorChar);
    }

    private static string? ParentOf(string path)
    {
        try { return Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch (ArgumentException) { return null; }
    }

    private static string LeafName(string path)
    {
        try { return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
