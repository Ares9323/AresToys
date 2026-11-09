using System.Globalization;
using Microsoft.Win32;
using AresToys.App.Resources;

namespace AresToys.App.Services.NewPlus;

/// <summary>
/// Registers the Explorer entry "New" for the empty area of a folder and of the desktop: ONE verb
/// with a cascading submenu that lists the templates, then a separator and "Open templates". Picking
/// a template runs <c>"arestoys.exe" --newplus "&lt;template&gt;" "%V"</c> (%V is the folder the menu was
/// opened in), followed by <c>--np-hide-digits</c> and <c>--np-replace-vars</c> when those options are
/// on. <see cref="NewPlusHeadless"/> handles it in <c>Program.Main</c>, so the entry works with the
/// app closed.
///
/// Pure registry like <see cref="ResizerContextMenuRegistration"/>, no shell extension. Layout:
/// <code>
/// HKCU\Software\Classes\Directory\Background\shell\AresToysNewPlus
///     MUIVerb = "New", Icon = "&lt;exe&gt;", Position = "Top", SubCommands = "", CommandsVersion = 2
///     shell\001_template\   MUIVerb = "&lt;title&gt;", Icon = "&lt;file,index&gt;"
///         command\(default) = "&lt;exe&gt;" --newplus "&lt;template&gt;" "%V" [--np-hide-digits] [--np-replace-vars]
///     shell\999_open\       MUIVerb = "Open templates", CommandFlags = 0x20 (separator above)
///         command\(default) = explorer.exe "&lt;template folder&gt;"
/// </code>
/// It also owns the switch that hides the built-in "New" entry. All under HKCU: no admin, no UAC.
/// </summary>
internal static class NewPlusContextMenuRegistration
{
    /// <summary>One template in the menu.</summary>
    public sealed record Entry(string TemplatePath, string Title, string? Icon);

    private const string VerbKeyPath = @"Software\Classes\Directory\Background\shell\AresToysNewPlus";
    private const string BuiltInNewKeyPath = @"Software\Classes\Directory\Background\ShellEx\ContextMenuHandlers\New";
    private const string DisabledPrefix = "disabled_";
    private const string OpenKeyName = "999_open";
    private const int SeparatorAbove = 0x20;
    private const int MaxTemplates = 998;

    // Bumped whenever the shape of the verb commands changes, so IsRegistered reports an older
    // registration as stale and the sync rewrites it once (1 = commands without option flags).
    private const string CommandsVersionValue = "CommandsVersion";
    private const int CommandsVersion = 2;

    /// <summary>True when the cascade points at the current exe's menu and carries the current command
    /// shape. A cascade left by another install location or by an older version reads as "not registered".</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(VerbKeyPath);
            return key?.GetValue("Icon") is string icon
                && string.Equals(icon, ExplorerContextMenuRegistration.CurrentExePath(), StringComparison.OrdinalIgnoreCase)
                && key.GetValue(CommandsVersionValue) is int version && version == CommandsVersion;
        }
        catch { return false; }
    }

    /// <summary>Rewrites the whole cascade: delete, then create, so a removed or renamed template
    /// leaves no trace. The two options travel in each command as flags, because the exe runs without
    /// the settings store. Throws on registry failures so the caller can log them.</summary>
    public static void Register(IReadOnlyList<Entry> entries, string templateFolder, bool hideStartingDigits, bool replaceVariables)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var exe = ExplorerContextMenuRegistration.CurrentExePath();

        Registry.CurrentUser.DeleteSubKeyTree(VerbKeyPath, throwOnMissingSubKey: false);
        using var verb = Registry.CurrentUser.CreateSubKey(VerbKeyPath);
        verb.SetValue("MUIVerb", ResolveString(nameof(Strings.NewPlusMenu_Label)));
        verb.SetValue("Icon", exe);
        verb.SetValue("Position", "Top");
        verb.SetValue("SubCommands", string.Empty);
        verb.SetValue(CommandsVersionValue, CommandsVersion, RegistryValueKind.DWord);

        var flags = (hideStartingDigits ? " " + NewPlusHeadless.HideDigitsFlag : string.Empty)
                    + (replaceVariables ? " " + NewPlusHeadless.ReplaceVarsFlag : string.Empty);
        using var sub = verb.CreateSubKey("shell");
        for (var i = 0; i < entries.Count && i < MaxTemplates; i++)
        {
            var entry = entries[i];
            WriteSubVerb(sub, $"{i + 1:D3}_template", entry.Title, entry.Icon,
                $"\"{exe}\" {NewPlusHeadless.NewPlusSwitch} \"{entry.TemplatePath}\" \"%V\"{flags}", separatorAbove: false);
        }
        WriteSubVerb(sub, OpenKeyName, ResolveString(nameof(Strings.NewPlusMenu_OpenTemplates)), null,
            $"explorer.exe \"{templateFolder}\"", separatorAbove: entries.Count > 0);

        ExplorerContextMenuRegistration.NotifyShell();
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(VerbKeyPath, throwOnMissingSubKey: false); }
        catch { /* best effort, like the other verbs */ }
        ExplorerContextMenuRegistration.NotifyShell();
    }

    /// <summary>True once this process hid the built-in "New": only that process gives it back when it ends, so a
    /// second instance that starts and leaves at once never shows it while the first one still runs.</summary>
    public static bool HiddenByThisProcess { get; private set; }

    /// <summary>Gives the built-in "New" (and the Ctrl+Shift+N of Windows with it) back while AresToys is not running:
    /// called when the app exits, when it crashes, and by the satellite when the main app dies under it. The next
    /// start hides it again through the New+ sync. Never throws: it runs on the way out.</summary>
    public static void RestoreBuiltInNewOnExit()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(BuiltInNewKeyPath, writable: true);
            if (key?.GetValue(null) is string value && value.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                key.DeleteValue(string.Empty, throwOnMissingValue: false);
        }
        catch { /* registry unavailable on the way out: the next start or the New+ tab sets it right */ }
    }

    /// <summary>Hides (or shows again) the built-in "New" entry of the folder background menu by
    /// pointing the per-user override of its handler at a name that is not a CLSID, which is what
    /// PowerToys does. Showing it again only removes an override that carries the marker, so a
    /// value someone else set is left alone. Throws on registry failures.</summary>
    public static void SetBuiltInNewHidden(bool hidden)
    {
        if (hidden)
        {
            using var key = Registry.CurrentUser.CreateSubKey(BuiltInNewKeyPath);
            key.SetValue(null, DisabledPrefix);
            HiddenByThisProcess = true;
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(BuiltInNewKeyPath, writable: true);
            if (key?.GetValue(null) is string value && value.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                key.DeleteValue(string.Empty, throwOnMissingValue: false);
        }
        ExplorerContextMenuRegistration.NotifyShell();
    }

    private static void WriteSubVerb(RegistryKey subVerbs, string keyName, string label, string? icon, string command, bool separatorAbove)
    {
        using var key = subVerbs.CreateSubKey(keyName);
        // A single '&' marks an accelerator in a menu text; double it to show the character itself.
        key.SetValue("MUIVerb", label.Replace("&", "&&", StringComparison.Ordinal));
        if (icon is not null) key.SetValue("Icon", icon);
        if (separatorAbove) key.SetValue("CommandFlags", SeparatorAbove, RegistryValueKind.DWord);
        using var commandKey = key.CreateSubKey("command");
        commandKey.SetValue(null, command);
    }

    /// <summary>The same culture the UI uses (the LocalizedStrings singleton), because
    /// <c>Strings.Culture</c> is not reliable after the host starts.</summary>
    private static string ResolveString(string key)
    {
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        return Strings.ResourceManager.GetString(key, culture) ?? key;
    }
}
