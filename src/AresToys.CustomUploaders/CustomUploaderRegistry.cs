using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.CustomUploaders;

/// <summary>Discovers <c>.sxcu</c> files in a folder, parses each into a
/// <see cref="CustomUploaderConfig"/>, and yields a <see cref="CustomUploader"/> for every valid
/// entry. The IDs are deterministic — derived from the config's <c>Name</c> + a short hash of the
/// file path — so toggling them on/off via <c>plugin.{id}.enabled</c> survives restarts and two
/// files named "Imgur" in different subfolders don't shadow each other.</summary>
public static class CustomUploaderRegistry
{
    /// <summary>Default folder under <c>%LOCALAPPDATA%\AresToys\</c>. Created on first use; missing
    /// is fine — yields no uploaders.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AresToys", "custom-uploaders");

    /// <summary>Walk <paramref name="folderPath"/>, parse every <c>.sxcu</c> (top-level + nested),
    /// and register each valid one into <paramref name="services"/> as
    /// <see cref="IUploader"/>. Per-file failures are isolated; the loader keeps walking and
    /// reports them via <paramref name="onError"/> (defaults to logger warnings).</summary>
    public static IReadOnlyList<CustomUploaderConfig> RegisterFromFolder(
        string folderPath,
        IServiceCollection services,
        ILogger? logger = null,
        Action<string, Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        logger ??= NullLogger.Instance;
        var loaded = new List<CustomUploaderConfig>();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return loaded;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folderPath, "*.sxcu", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException ex) { onError?.Invoke(folderPath, ex); return loaded; }
        catch (IOException ex) { onError?.Invoke(folderPath, ex); return loaded; }

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var config = CustomUploaderConfigLoader.Parse(json);
                if (!CustomUploaderConfigLoader.IsValid(config))
                {
                    logger.LogWarning("Skipping invalid .sxcu file (missing Name or RequestURL): {File}", file);
                    continue;
                }

                var id = BuildStableId(config!, file);
                services.AddSingleton<IUploader>(sp =>
                {
                    // HttpClient lifetime: one per uploader is fine (Singleton scope) — shared
                    // socket pool, no per-request creation overhead. We could route through
                    // IHttpClientFactory but keeping it simple keeps the registration shape
                    // identical to the bundled Catbox/Litterbox uploaders.
                    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    var log = sp.GetService<ILogger<CustomUploader>>();
                    return new CustomUploader(config!, id, http, log);
                });
                loaded.Add(config!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(file, ex);
            }
        }
        return loaded;
    }

    /// <summary>Stable ID for a custom uploader: <c>"custom.&lt;slug&gt;.&lt;hash&gt;"</c> where
    /// slug is the lowercased Name and hash is the first 6 chars of SHA-256(filepath). The
    /// hash component prevents collisions when two files happen to share the same Name (e.g.
    /// the user keeps an old + new version of an Imgur config).</summary>
    public static string BuildStableId(CustomUploaderConfig config, string filePath)
    {
        var slug = Slugify(config.Name ?? "uploader");
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(filePath));
        var hash = Convert.ToHexString(hashBytes)[..6].ToLowerInvariant();
        return $"custom.{slug}.{hash}";
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "uploader" : s;
    }
}
