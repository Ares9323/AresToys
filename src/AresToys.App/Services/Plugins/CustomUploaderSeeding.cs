using System.IO;
using AresToys.CustomUploaders;

namespace AresToys.App.Services.Plugins;

/// <summary>Copies the bundled <c>.sxcu</c> defaults into the user's custom-uploaders folder
/// the first time each one is seen. Per-file tracking via a manifest line means:
/// <list type="bullet">
/// <item><description>User deletes a seeded default → stays deleted (its name is in the manifest,
///     we don't reseed).</description></item>
/// <item><description>New version of the app ships extra defaults → those names aren't in the
///     manifest yet, get copied on next startup.</description></item>
/// <item><description>User edits a seeded file → our seed doesn't overwrite (we only copy when
///     the destination doesn't exist).</description></item>
/// </list></summary>
public static class CustomUploaderSeeding
{
    /// <summary>Per-file seed manifest. Lives in the user's custom-uploaders folder, one
    /// asset filename per line. The leading "#" line is a human-readable note.</summary>
    private const string SeedManifestFileName = ".seeded";

    /// <summary>Idempotent. Compares the bundled <c>Assets/CustomUploaders/*.sxcu</c> against
    /// the per-file manifest, copies anything new (and not already present at the destination),
    /// and appends those names to the manifest. Safe to call on every startup.</summary>
    public static void EnsureDefaults()
    {
        var targetFolder = CustomUploaderRegistry.DefaultFolder;
        var manifestPath = Path.Combine(targetFolder, SeedManifestFileName);

        try
        {
            Directory.CreateDirectory(targetFolder);

            var sourceFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "CustomUploaders");
            if (!Directory.Exists(sourceFolder)) return; // dev build without assets — no-op

            // Already-seeded names are read once. HashSet for O(1) lookup, case-insensitive
            // to match Windows file-system semantics.
            var seeded = ReadSeededNames(manifestPath);
            var newlySeeded = new List<string>();

            foreach (var src in Directory.EnumerateFiles(sourceFolder, "*.sxcu"))
            {
                var name = Path.GetFileName(src);
                if (seeded.Contains(name)) continue; // already seeded once — respect user delete
                var dest = Path.Combine(targetFolder, name);
                if (File.Exists(dest))
                {
                    // User already has a same-named file (manual import?) — don't clobber, but
                    // still record so we don't re-evaluate every startup.
                    newlySeeded.Add(name);
                    continue;
                }
                File.Copy(src, dest);
                newlySeeded.Add(name);
            }

            if (newlySeeded.Count > 0) AppendManifest(manifestPath, newlySeeded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[CustomUploaderSeeding] failed: {ex.Message}");
        }
    }

    private static HashSet<string> ReadSeededNames(string manifestPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath)) return set;
        foreach (var raw in File.ReadAllLines(manifestPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            set.Add(line);
        }
        return set;
    }

    private static void AppendManifest(string manifestPath, IEnumerable<string> namesToAdd)
    {
        var header = !File.Exists(manifestPath)
            ? $"# Custom uploader seed manifest. One file name per line — listed names won't be reseeded.\n# Created {DateTimeOffset.UtcNow:O}.\n"
            : string.Empty;
        File.AppendAllText(manifestPath, header + string.Join('\n', namesToAdd) + "\n");
    }
}
