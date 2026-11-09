using System.Globalization;
using Microsoft.Win32;
using AresToys.App.Resources;

namespace AresToys.App.Services;

/// <summary>
/// Registers the Explorer entry "Image Resizer" (with the AresToys icon) for image files: ONE verb with a cascading
/// submenu (the arrow on the right) that lists the Resizer workflows and, last, "Custom...".
/// Picking an item runs <c>"arestoys.exe" --resize &lt;workflowId|custom&gt; "&lt;file&gt;"</c>, which
/// the app already handles (see <see cref="SingleInstanceGuard.ResizePrefix"/>).
///
/// The cascade is pure registry, no shell extension. Layout written for every extension in
/// <see cref="Extensions"/>:
/// <code>
/// HKCU\Software\Classes\SystemFileAssociations\&lt;ext&gt;\shell\AresToysResize
///     MUIVerb          = "Image Resizer"         (the AresToys exe icon tells it apart from PowerToys)
///     Icon             = "&lt;exe&gt;"
///     SubCommands      = ""                      (empty: the sub verbs live in the nested shell key)
///     MultiSelectModel = "Document"              (one process per selected file, the app batches them)
///     shell\001_workflow\                        (one per workflow, ordered by key name)
///         MUIVerb = "&lt;display name&gt;", MultiSelectModel = "Document"
///         command\(default) = "&lt;exe&gt;" --resize "&lt;workflowId&gt;" "%1"
///     shell\999_custom\                          (always last, with a separator above it)
///         MUIVerb = "Custom...", CommandFlags = 0x20 (DWORD), MultiSelectModel = "Document"
///         command\(default) = "&lt;exe&gt;" --resize custom "%1"
/// </code>
/// Key names are ordinal numbers, never workflow ids, so ids with odd characters cannot break the
/// registry path. Everything is under HKCU, so no admin and no UAC. Like the upload verb, on
/// Windows 11 it appears under "Show more options".
/// </summary>
public static class ResizerContextMenuRegistration
{
    /// <summary>The image types the resize pipeline reads. Nothing is registered for other files.</summary>
    public static readonly IReadOnlyList<string> Extensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif", ".webp"];

    /// <summary>The value the app's <c>--resize</c> receiver treats as "show the Custom dialog".</summary>
    public const string CustomToken = "custom";

    private const string AssociationsRoot = @"Software\Classes\SystemFileAssociations";
    private const string VerbName = "AresToysResize";
    private const string CustomKeyName = "999_custom";
    private const string CommandKeyName = "command";
    private const string SubVerbsKeyName = "shell";
    private const string MultiSelectModel = "Document";
    // CommandFlags bit that draws a separator line above the sub verb.
    private const int SeparatorAbove = 0x20;
    // Three digit ordinals keep key-name order equal to list order, with room below "999_custom".
    private const int MaxWorkflows = 998;

    /// <summary>True when the Custom sub verb of the first extension points at the current exe.
    /// A cascade left by another install location reads as "not registered".</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"{VerbKeyPath(Extensions[0])}\{SubVerbsKeyName}\{CustomKeyName}\{CommandKeyName}");
            var command = key?.GetValue(null) as string;
            return command is not null
                && command.Contains(ExplorerContextMenuRegistration.CurrentExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Rewrite the whole cascade for every extension: delete, then create, so a workflow
    /// that was removed or renamed leaves no trace. <paramref name="workflows"/> is already in
    /// display order. Throws on registry failures so the caller can log or surface them.</summary>
    public static void Register(IReadOnlyList<(string Id, string Name)> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        var exe = ExplorerContextMenuRegistration.CurrentExePath();
        var label = ResolveString(nameof(Strings.ResizerMenu_Label));
        var customLabel = ResolveString(nameof(Strings.ResizerMenu_Custom));

        foreach (var extension in Extensions)
        {
            var verbPath = VerbKeyPath(extension);
            Registry.CurrentUser.DeleteSubKeyTree(verbPath, throwOnMissingSubKey: false);

            using var verbKey = Registry.CurrentUser.CreateSubKey(verbPath);
            verbKey.SetValue("MUIVerb", label);
            verbKey.SetValue("Icon", exe);
            verbKey.SetValue("SubCommands", string.Empty);
            verbKey.SetValue("MultiSelectModel", MultiSelectModel);

            using var subVerbs = verbKey.CreateSubKey(SubVerbsKeyName);
            for (var i = 0; i < workflows.Count && i < MaxWorkflows; i++)
            {
                var (id, name) = workflows[i];
                WriteSubVerb(subVerbs, $"{i + 1:D3}_workflow", name, $"\"{exe}\" --resize \"{id}\" \"%1\"", separatorAbove: false);
            }
            WriteSubVerb(subVerbs, CustomKeyName, customLabel, $"\"{exe}\" --resize {CustomToken} \"%1\"",
                separatorAbove: workflows.Count > 0);
        }
        ExplorerContextMenuRegistration.NotifyShell();
    }

    /// <summary>Remove the cascade from every extension. Best effort: a missing key is fine.</summary>
    public static void Unregister()
    {
        foreach (var extension in Extensions)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(VerbKeyPath(extension), throwOnMissingSubKey: false); }
            catch { /* best-effort, like the upload verb */ }
        }
        ExplorerContextMenuRegistration.NotifyShell();
    }

    private static void WriteSubVerb(RegistryKey subVerbs, string keyName, string label, string command, bool separatorAbove)
    {
        using var key = subVerbs.CreateSubKey(keyName);
        // A single '&' in a menu text marks an accelerator; double it to show the literal character.
        key.SetValue("MUIVerb", label.Replace("&", "&&", StringComparison.Ordinal));
        key.SetValue("MultiSelectModel", MultiSelectModel);
        if (separatorAbove) key.SetValue("CommandFlags", SeparatorAbove, RegistryValueKind.DWord);
        using var commandKey = key.CreateSubKey(CommandKeyName);
        // %1 = the file path Explorer hands us; the quotes survive paths with spaces.
        commandKey.SetValue(null, command);
    }

    private static string VerbKeyPath(string extension) => $@"{AssociationsRoot}\{extension}\shell\{VerbName}";

    /// <summary>The same culture the UI uses (the LocalizedStrings singleton), because
    /// <c>Strings.Culture</c> is not reliable after the host starts.</summary>
    private static string ResolveString(string key)
    {
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        return Strings.ResourceManager.GetString(key, culture) ?? key;
    }
}
