namespace AresToys.AI.Dictation;

/// <summary>The languages dictation offers, as the words stored in the settings and the codes whisper takes.
/// One language at a time: letting whisper guess on a short phrase often picks the wrong one.</summary>
public static class DictationLanguages
{
    public static readonly IReadOnlyList<(string Key, string Code, string Label)> All =
    [
        ("english", "en", "English"),
        ("italian", "it", "Italiano"),
        ("spanish", "es", "Español"),
        ("french", "fr", "Français"),
        ("german", "de", "Deutsch"),
    ];

    public const string Default = "english";

    /// <summary>Whisper's code for a stored key; an unknown key falls back to English.</summary>
    public static string CodeFor(string? key)
    {
        foreach (var language in All)
        {
            if (string.Equals(language.Key, key, StringComparison.OrdinalIgnoreCase)) return language.Code;
        }
        return "en";
    }
}
