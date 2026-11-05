using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Peek;

/// <summary>How the user chose to show a file extension in Peek ("Show as" in the footer). A pick is an id string
/// stored per extension: <c>text</c>, <c>json</c>, <c>xml</c>, <c>yaml</c>, <c>ini</c>, <c>markdown</c>, <c>html</c>,
/// <c>shell</c> (the Windows preview), <c>image</c>, or <c>code:&lt;highlight.js id&gt;</c>. No entry means Auto.</summary>
internal static class PeekShowAs
{
    public const string Text = "text";
    public const string Json = "json";
    public const string Xml = "xml";
    public const string Yaml = "yaml";
    public const string Ini = "ini";
    public const string Markdown = "markdown";
    public const string Html = "html";
    public const string Shell = "shell";
    public const string Image = "image";
    public const string CodePrefix = "code:";

    /// <summary>The picks of the current process: replaced whole by <see cref="Apply"/>, read by
    /// <see cref="PeekPreview.For"/> and <see cref="PeekPreview.LanguageFor"/> from any thread.</summary>
    private static volatile IReadOnlyDictionary<string, string> _picks = new Dictionary<string, string>();

    /// <summary>Makes <paramref name="picks"/> (lower case extension with the dot to pick id) the picks Peek uses.</summary>
    public static void Apply(IReadOnlyDictionary<string, string> picks)
    {
        ArgumentNullException.ThrowIfNull(picks);
        _picks = new Dictionary<string, string>(picks, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The pick id for the extension of <paramref name="path"/>, or null (Auto). A file with no extension
    /// never has one.</summary>
    public static string? PickFor(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && _picks.TryGetValue(extension, out var pick) && Resolve(pick) is not null ? pick : null;
    }

    /// <summary>The kind and (for code) the language a pick id stands for, or null when the id is not known.</summary>
    public static (PeekPreviewKind Kind, PeekLanguage? Language)? Resolve(string? pick)
    {
        if (string.IsNullOrEmpty(pick)) return null;
        switch (pick)
        {
            case Text: return (PeekPreviewKind.Code, new PeekLanguage("plaintext", "Text"));
            case Json: return (PeekPreviewKind.Code, new PeekLanguage("json", "JSON"));
            case Xml: return (PeekPreviewKind.Code, new PeekLanguage("xml", "XML"));
            case Yaml: return (PeekPreviewKind.Code, new PeekLanguage("yaml", "YAML"));
            case Ini: return (PeekPreviewKind.Code, new PeekLanguage("ini", "INI"));
            case Markdown: return (PeekPreviewKind.Markdown, null);
            case Html: return (PeekPreviewKind.Html, null);
            case Shell: return (PeekPreviewKind.Shell, null);
            case Image: return (PeekPreviewKind.Image, null);
        }

        if (pick.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            var id = pick[CodePrefix.Length..];
            foreach (var language in PeekPreview.CodeLanguageChoices)
            {
                if (language.Id == id) return (PeekPreviewKind.Code, language);
            }
        }

        return null;
    }

    /// <summary>The name of a pick for the footer button and the settings list; the id itself when unknown.</summary>
    public static string DisplayName(string pick) => pick switch
    {
        Text => AresToys.App.Resources.Strings.Peek_ShowAsText,
        Shell => AresToys.App.Resources.Strings.Peek_ShowAsShell,
        Image => AresToys.App.Resources.Strings.Peek_ShowAsImage,
        _ => Resolve(pick) is { Language: { } language } ? language.DisplayName
            : Resolve(pick) is { Kind: PeekPreviewKind.Markdown } ? "Markdown"
            : Resolve(pick) is { Kind: PeekPreviewKind.Html } ? "HTML"
            : pick,
    };

    /// <summary>The picks under the "Show as" menu before the code languages, in menu order (Windows preview and Image
    /// are added by the window only when the file allows them).</summary>
    public static readonly IReadOnlyList<string> BasicPicks = new[] { Text, Json, Xml, Yaml, Ini, Markdown, Html };
}

/// <summary>Reads and writes the "Show as" picks in the settings store, key <see cref="SettingKey"/>: a JSON map of
/// lower case extension (with the dot) to pick id. Nothing is cached: the satellite loads it at each Peek, so a
/// change made in Settings applies to the next one.</summary>
public sealed class PeekShowAsStore(ISettingsStore settings, ILogger<PeekShowAsStore>? logger = null)
{
    public const string SettingKey = "peek.show_as";

    /// <summary>The stored picks; empty when the key is missing or its JSON is invalid (logged).</summary>
    public async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(SettingKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            return map is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Peek show as: the stored picks are not valid JSON, using none");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public Task SaveAsync(IReadOnlyDictionary<string, string> picks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(picks);
        return settings.SetAsync(SettingKey, JsonSerializer.Serialize(picks), sensitive: false, ct);
    }

    /// <summary>Sets (<paramref name="pick"/> given) or removes (null, Auto) the pick of one extension and saves.
    /// Returns the map as saved. <paramref name="extension"/> is lower cased.</summary>
    public async Task<Dictionary<string, string>> SetAsync(string extension, string? pick, CancellationToken ct = default)
    {
        var key = extension.ToLowerInvariant();
        var map = await LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pick)) map.Remove(key);
        else map[key] = pick;
        await SaveAsync(map, ct).ConfigureAwait(false);
        return map;
    }
}
