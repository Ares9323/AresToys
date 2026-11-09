using System.Reflection;
using Microsoft.Extensions.Options;
using AresToys.Storage.Options;

namespace AresToys.Storage.Paths;

public sealed class StoragePathResolver : IStoragePathResolver
{
    private const string PortableMarkerFileName = "portable.txt";
    /// <summary>0.1.0 stored user data in <c>%LocalAppData%\AresToys\</c> directly, which
    /// COLLIDED with Velopack's install root (Velopack uses <c>%LocalAppData%\&lt;packId&gt;\</c>
    /// for binaries / packages / Update.exe). A reinstall would rebuild the parent dir and
    /// wipe our SQLite DB along with it, resetting every user setting. From 0.1.1 onward the
    /// user data lives at the sibling path <see cref="UserDataFolderName"/> so Velopack and
    /// our store no longer share a parent. The legacy constant survives for the one-shot
    /// migration in <see cref="MigrateLegacyUserDataIfPresent"/>.</summary>
    private const string LegacyAppDataFolderName = "AresToys";
    private const string UserDataFolderName = "AresToys-Data";

    /// <summary>Files / subdirectories owned by us (never by Velopack) that the migration
    /// can safely copy across from the legacy parent. Velopack puts <c>current/</c>,
    /// <c>packages/</c>, <c>Update.exe</c> in the same legacy parent — those MUST be left
    /// alone or the installed app's binaries get tangled.</summary>
    private static readonly string[] OurFileNames =
    {
        "arestoys.db",
        "arestoys.db-shm",
        "arestoys.db-wal",
    };
    private static readonly string[] OurDirectoryNames =
    {
        "blobs",
    };

    private readonly StorageOptions _options;

    public StoragePathResolver(IOptions<StorageOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public string ResolveRoot()
    {
        var root = _options.RootDirectoryOverride ?? DefaultRoot();
        Directory.CreateDirectory(root);
        return root;
    }

    public string ResolveDatabasePath()
    {
        var root = ResolveRoot();
        var path = Path.Combine(root, _options.DatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public string ResolveBlobRoot()
    {
        var blobRoot = Path.Combine(ResolveRoot(), _options.BlobSubdirectory);
        Directory.CreateDirectory(blobRoot);
        return blobRoot;
    }

    private static string DefaultRoot()
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var portableMarker = Path.Combine(assemblyDir, PortableMarkerFileName);
        if (File.Exists(portableMarker))
        {
            return Path.Combine(assemblyDir, "data");
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var newRoot = Path.Combine(localAppData, UserDataFolderName);
        var legacyRoot = Path.Combine(localAppData, LegacyAppDataFolderName);
        MigrateLegacyUserDataIfPresent(legacyRoot, newRoot);
        return newRoot;
    }

    /// <summary>One-shot copy from the legacy data folder to the new location, executed
    /// on every cold start (early-exits when the migration is already done). Copies only
    /// our whitelisted files / directories — Velopack's <c>current/</c>, <c>packages/</c>,
    /// and <c>Update.exe</c> stay where they are because we'd brick the installed app
    /// otherwise. We <em>copy</em> rather than move so the legacy data lingers as a
    /// fallback if anything goes wrong; subsequent Velopack reinstalls may delete it
    /// later and that's fine because the canonical store is the new path by then.
    /// <para>Best-effort: a failure (locked file, permission denied) silently leaves the
    /// new dir empty so the app starts fresh — better than crashing on startup.</para></summary>
    private static void MigrateLegacyUserDataIfPresent(string legacyRoot, string newRoot)
    {
        if (Directory.Exists(newRoot)) return;        // already migrated (or fresh install)
        if (!Directory.Exists(legacyRoot)) return;    // no legacy data to migrate

        try
        {
            Directory.CreateDirectory(newRoot);
            foreach (var name in OurFileNames)
            {
                var src = Path.Combine(legacyRoot, name);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(newRoot, name);
                File.Copy(src, dst, overwrite: false);
            }
            foreach (var dirName in OurDirectoryNames)
            {
                var srcDir = Path.Combine(legacyRoot, dirName);
                if (!Directory.Exists(srcDir)) continue;
                CopyDirectoryRecursive(srcDir, Path.Combine(newRoot, dirName));
            }
        }
        catch
        {
            // Swallow — the app should still launch. Worst case the user starts with
            // empty history and has to re-configure; better than a hard crash on every
            // first run after the upgrade.
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var f in Directory.EnumerateFiles(sourceDir))
        {
            var dst = Path.Combine(destDir, Path.GetFileName(f));
            if (!File.Exists(dst)) File.Copy(f, dst, overwrite: false);
        }
        foreach (var d in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(d, Path.Combine(destDir, Path.GetFileName(d)));
        }
    }
}
