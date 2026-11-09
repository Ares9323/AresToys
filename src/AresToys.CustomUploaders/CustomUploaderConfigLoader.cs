using System.Text.Json;

namespace AresToys.CustomUploaders;

/// <summary>Reads and validates <c>.sxcu</c> files. ShareX uses standard JSON (sometimes with
/// trailing commas / comments — ShareX itself is permissive), so we configure
/// <see cref="JsonSerializerOptions"/> to match.</summary>
public static class CustomUploaderConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Parse a JSON string (the contents of an .sxcu file) into a config. Returns null
    /// when the JSON is malformed or empty; throws nothing — the loader is meant to keep walking
    /// a folder of files even when individual entries are broken.</summary>
    public static CustomUploaderConfig? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CustomUploaderConfig>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Read + parse a single file. Empty / malformed files return null.</summary>
    public static async Task<CustomUploaderConfig?> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;
        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Validate a config has the minimum fields needed to actually run an upload.
    /// <c>Name</c> + <c>RequestURL</c> are mandatory; everything else has sane defaults.</summary>
    public static bool IsValid(CustomUploaderConfig? config) =>
        config is not null
        && !string.IsNullOrWhiteSpace(config.Name)
        && !string.IsNullOrWhiteSpace(config.RequestURL);
}
