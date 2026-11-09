using System.Text.Json;

namespace AresToys.ImageEffects.Serialization;

/// <summary>One-way converter from ShareX <c>.sxie</c> JSON content into our
/// <see cref="EffectPreset"/>. ShareX serialises with Newtonsoft + <c>TypeNameHandling.Auto</c>,
/// so each effect is keyed by a <c>$type</c> string of the form
/// <c>"ShareX.ImageEffectsLib.Brightness, ShareX.ImageEffectsLib"</c> (legacy) or
/// <c>"ShareX.ImageEditor.Core.ImageEffects.Adjustments.BrightnessImageEffect, ShareX.ImageEditor"</c>
/// (modern). We strip the assembly suffix, take the short class name, drop the optional
/// <c>ImageEffect</c> tail, then camel-case the result to produce our <see cref="ImageEffect.Id"/>.
/// Unknown <c>$type</c>s are silently skipped — a preset that mixes ported and not-yet-ported
/// effects still lands the parts we understand.
///
/// This reader handles raw JSON only. The full <c>.sxie</c> ZIP package (with bundled assets
/// for watermark images) lands in a follow-up step.</summary>
public static class SxiePresetImporter
{
    /// <summary>Class names whose default heuristic mapping (<c>strip ImageEffect</c> +
    /// camel-case) does NOT match a registered id. Populated case-by-case as we hit
    /// divergences during testing.</summary>
    private static readonly Dictionary<string, string> _explicitMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Examples — fill in when we add effects whose legacy ShareX name doesn't follow the
        // <code>ClassName + ImageEffect</code> pattern, e.g. ShareX's "MatrixColors" maps to
        // our "colorMatrix" once the ColorMatrix effect is ported.
    };

    public static EffectPreset Import(string json, ImageEffectRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var reg = registry ?? ImageEffectRegistry.Default;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var preset = new EffectPreset
        {
            Name = root.TryGetProperty("Name", out var nameEl) ? (nameEl.GetString() ?? string.Empty) : string.Empty,
        };

        if (!root.TryGetProperty("Effects", out var effectsEl) || effectsEl.ValueKind != JsonValueKind.Array)
            return preset;

        foreach (var entryEl in effectsEl.EnumerateArray())
        {
            if (entryEl.ValueKind != JsonValueKind.Object) continue;
            if (!entryEl.TryGetProperty("$type", out var typeEl)) continue;

            var typeStr = typeEl.GetString();
            if (string.IsNullOrWhiteSpace(typeStr)) continue;

            var id = ResolveId(typeStr);
            if (id is null) continue;

            var effect = reg.Create(id);
            if (effect is null) continue;

            // ShareX names properties PascalCase (Amount, Strength). Our serializer's
            // ApplyProperties already falls back from camelCase to raw PropertyName, so it
            // accepts both shapes — we can reuse it here without translation.
            EffectPropertyBinder.Apply(effect, entryEl);

            var enabled = !entryEl.TryGetProperty("Enabled", out var enEl) || enEl.GetBoolean();
            preset.Effects.Add(new EffectPresetEntry(effect, enabled));
        }

        return preset;
    }

    /// <summary>Map a ShareX <c>$type</c> string to our id, or null if we can't resolve it.
    /// Modern ShareX (and our codebase) use snake_case ids — <c>rounded_corners</c>,
    /// <c>auto_contrast</c>, <c>draw_background</c> — so we PascalCase → snake_case the class
    /// name (after stripping the common <c>"ImageEffect"</c>/<c>"Effect"</c> suffixes used by
    /// both the legacy <c>ImageEffectsLib</c> and the new <c>ImageEditor</c> namespaces).
    /// Public so the unit-test project can pin the snake-casing rules without taking a
    /// dependency on InternalsVisibleTo plumbing.</summary>
    public static string? ResolveId(string typeName)
    {
        var commaIdx = typeName.IndexOf(',');
        var fullName = commaIdx >= 0 ? typeName[..commaIdx].Trim() : typeName.Trim();
        var dotIdx = fullName.LastIndexOf('.');
        var shortName = dotIdx >= 0 ? fullName[(dotIdx + 1)..] : fullName;

        if (_explicitMap.TryGetValue(shortName, out var explicitId)) return explicitId;

        // Strip "ImageEffect" first (longer suffix wins; ShareX moderno uses both forms).
        var stripped = shortName.EndsWith("ImageEffect", StringComparison.Ordinal)
            ? shortName[..^"ImageEffect".Length]
            : shortName.EndsWith("Effect", StringComparison.Ordinal)
                ? shortName[..^"Effect".Length]
                : shortName;
        if (stripped.Length == 0) return null;

        return PascalToSnake(stripped);
    }

    private static string PascalToSnake(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            // Insert an underscore before every uppercase letter that follows a lowercase
            // letter or precedes a lowercase letter — handles RoundedCorners → rounded_corners
            // and the acronym edge case ABCDef → abc_def.
            if (i > 0 && char.IsUpper(c)
                && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
