using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AresToys.CustomUploaders;

/// <summary>Template substitution engine for <c>.sxcu</c> files. Two phases:
/// <list type="number">
/// <item><description><b>Pre-request</b>: tokens that don't depend on a server response —
///     <c>{filename}</c>, <c>{filename:noext}</c>, <c>{rndhex:N}</c>, <c>{random:abc}</c>.</description></item>
/// <item><description><b>Post-request</b>: response-aware tokens — <c>{response}</c>,
///     <c>{json:path}</c>, <c>{regex:pattern|group}</c>.</description></item>
/// </list>
/// The engine is deliberately tolerant: unknown / unresolvable tokens collapse to empty string
/// instead of throwing. ShareX behaves the same, and a stray <c>{json:foo}</c> in an URL
/// template shouldn't blow the whole upload up — failure surfaces as "URL template produced
/// empty result" upstream.</summary>
public sealed class CustomUploaderTemplate
{
    private static readonly Regex TokenRegex = new(@"\{([a-z0-9_]+)(?::([^}]*))?\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Random Random = new();

    private readonly string _fileName;
    private readonly string? _input;
    private readonly string? _responseBody;
    private readonly JsonElement? _responseJson;

    /// <summary>Pre-request context — only the file name is known at this stage.</summary>
    public CustomUploaderTemplate(string fileName) : this(fileName, input: null, responseBody: null) { }

    /// <summary>Pre-request context with the payload as text. <paramref name="input"/> is the
    /// UTF-8 string of the payload bytes — required for <c>{input}</c> token (used by text /
    /// URL-shortener .sxcu where the body itself is what gets uploaded). Pass null when the
    /// payload isn't reasonably representable as text (binary file uploads).</summary>
    public CustomUploaderTemplate(string fileName, string? input)
        : this(fileName, input, responseBody: null) { }

    /// <summary>Post-request context — full body available for <c>{response}</c> and friends.
    /// JSON is parsed lazily once and cached so repeated <c>{json:…}</c> tokens are cheap.</summary>
    public CustomUploaderTemplate(string fileName, string? input, string? responseBody)
    {
        _fileName = fileName ?? string.Empty;
        _input = input;
        _responseBody = responseBody;

        if (!string.IsNullOrEmpty(responseBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                _responseJson = doc.RootElement.Clone();
            }
            catch (JsonException) { _responseJson = null; }
        }
    }

    /// <summary>Apply substitutions to <paramref name="template"/>. Empty / null input passes
    /// through unchanged so callers can safely use it on every config field.</summary>
    public string Apply(string? template)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;

        return TokenRegex.Replace(template, m =>
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            var arg = m.Groups[2].Success ? m.Groups[2].Value : null;
            return Resolve(name, arg) ?? m.Value;
        });
    }

    /// <summary>Bulk substitute every value in a key/value collection (used for headers /
    /// parameters / multipart arguments). Keys are NOT templated — only values.</summary>
    public Dictionary<string, string> ApplyAll(IDictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null) return result;
        foreach (var (k, v) in source) result[k] = Apply(v);
        return result;
    }

    private string? Resolve(string name, string? arg) => name switch
    {
        "filename" => arg switch
        {
            null      => _fileName,
            "noext"   => Path.GetFileNameWithoutExtension(_fileName),
            "ext"     => Path.GetExtension(_fileName).TrimStart('.'),
            _         => _fileName, // unknown sub-arg — fall through to raw filename
        },
        // Some .sxcu files use the legacy unnamed alias.
        "filenamenoext" => Path.GetFileNameWithoutExtension(_fileName),
        // Payload as text — used by text/URL-shortener .sxcu (Pastebin's api_paste_code,
        // TinyURL's url field, Hastebin's body, etc.). Empty when the uploader was constructed
        // for a binary file (image / video / arbitrary file) where treating the bytes as a
        // string would produce garbage.
        "input"         => _input ?? string.Empty,
        "response"      => _responseBody ?? string.Empty,
        "json"          => arg is null ? string.Empty : ResolveJsonPath(arg),
        "regex"         => arg is null ? string.Empty : ResolveRegex(arg),
        "rndhex"        => RandomHex(int.TryParse(arg, out var n) ? n : 8),
        "random"        => RandomChar(arg),
        _ => null, // unknown token — leave the original {name:arg} in place
    };

    /// <summary>Walk a dotted path through the parsed JSON response. Bracketed indices
    /// (<c>data.images[0].url</c>) are also accepted. Missing keys / type mismatches return
    /// empty string.</summary>
    private string ResolveJsonPath(string path)
    {
        if (_responseJson is not { } root) return string.Empty;
        var current = root;
        foreach (var segment in SplitJsonPath(path))
        {
            if (segment.IsIndex)
            {
                if (current.ValueKind != JsonValueKind.Array) return string.Empty;
                if (segment.Index >= current.GetArrayLength()) return string.Empty;
                current = current[segment.Index];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object) return string.Empty;
                if (!current.TryGetProperty(segment.Name, out var next)) return string.Empty;
                current = next;
            }
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            JsonValueKind.Null   => string.Empty,
            _                    => current.GetRawText(),
        };
    }

    private static IEnumerable<JsonPathSegment> SplitJsonPath(string path)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var ch = path[i];
            if (ch == '.')
            {
                if (sb.Length > 0) { yield return JsonPathSegment.Property(sb.ToString()); sb.Clear(); }
            }
            else if (ch == '[')
            {
                if (sb.Length > 0) { yield return JsonPathSegment.Property(sb.ToString()); sb.Clear(); }
                var end = path.IndexOf(']', i + 1);
                if (end < 0) yield break;
                if (int.TryParse(path[(i + 1)..end], out var idx))
                    yield return JsonPathSegment.Indexed(idx);
                i = end;
            }
            else
            {
                sb.Append(ch);
            }
        }
        if (sb.Length > 0) yield return JsonPathSegment.Property(sb.ToString());
    }

    private readonly record struct JsonPathSegment(bool IsIndex, string Name, int Index)
    {
        public static JsonPathSegment Property(string n) => new(false, n, 0);
        public static JsonPathSegment Indexed(int i) => new(true, string.Empty, i);
    }

    /// <summary>Run a regex against the response body and return the chosen capture group.
    /// ShareX syntax: <c>{regex:pattern|group}</c>; <c>group</c> is either an integer or a
    /// named group. Missing group / no match → empty string.</summary>
    private string ResolveRegex(string arg)
    {
        if (string.IsNullOrEmpty(_responseBody)) return string.Empty;
        var pipeIdx = arg.LastIndexOf('|');
        var pattern = pipeIdx > 0 ? arg[..pipeIdx] : arg;
        var groupSpec = pipeIdx > 0 ? arg[(pipeIdx + 1)..] : "0";
        try
        {
            var match = Regex.Match(_responseBody, pattern);
            if (!match.Success) return string.Empty;
            return int.TryParse(groupSpec, out var idx)
                ? (idx < match.Groups.Count ? match.Groups[idx].Value : string.Empty)
                : match.Groups[groupSpec].Value;
        }
        catch (RegexMatchTimeoutException) { return string.Empty; }
        catch (ArgumentException) { return string.Empty; } // bad pattern
    }

    private static string RandomHex(int length)
    {
        const string Hex = "0123456789abcdef";
        var sb = new StringBuilder(length);
        lock (Random) for (var i = 0; i < length; i++) sb.Append(Hex[Random.Next(Hex.Length)]);
        return sb.ToString();
    }

    private static string RandomChar(string? alphabet)
    {
        if (string.IsNullOrEmpty(alphabet)) return string.Empty;
        lock (Random) return alphabet[Random.Next(alphabet.Length)].ToString();
    }
}
