using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using AresToys.App.Resources;

namespace AresToys.App.Services.NewPlus;

/// <summary>
/// The whole <c>arestoys.exe --newplus "&lt;template&gt;" "&lt;folder&gt;"</c> job, run from
/// <c>Program.Main</c> before the WPF app exists: no host, no database, no single instance guard,
/// no running AresToys needed. The two options the copy depends on arrive as flags in the command
/// (<see cref="NewPlusContextMenuRegistration"/> writes them from the settings), so nothing is read
/// from the settings store here.
/// </summary>
internal static class NewPlusHeadless
{
    internal const string NewPlusSwitch = "--newplus";
    internal const string HideDigitsFlag = "--np-hide-digits";
    internal const string ReplaceVarsFlag = "--np-replace-vars";

    private static readonly TimeSpan RenameWait = TimeSpan.FromSeconds(8);

    internal sealed record NewPlusRequest(string Template, string Folder, bool HideStartingDigits, bool ReplaceVariables);

    /// <summary>Finds <c>--newplus</c> followed by a template and a folder. False when the switch is
    /// absent or fewer than two values follow it.</summary>
    internal static bool TryParse(string[] args, out NewPlusRequest request)
    {
        request = null!;
        for (var i = 0; i < args.Length - 2; i++)
        {
            if (!string.Equals(args[i], NewPlusSwitch, StringComparison.OrdinalIgnoreCase)) continue;

            var template = args[i + 1];
            // A drive root arrives as C:\" (the backslash escapes the closing quote): repair it.
            var folder = NewPlusTemplates.RepairQuotedFolderArg(args[i + 2]);
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(folder)) return false;

            var rest = args.Skip(i + 3).ToList();
            request = new NewPlusRequest(
                template, folder,
                rest.Contains(HideDigitsFlag, StringComparer.OrdinalIgnoreCase),
                rest.Contains(ReplaceVarsFlag, StringComparer.OrdinalIgnoreCase));
            return true;
        }
        return false;
    }

    /// <summary>Handles the request when <paramref name="args"/> carries one: creates the copy, puts it
    /// in rename mode and waits for that to finish. A failure shows one message box. Returns false only
    /// when there is no (valid) <c>--newplus</c> request, so the caller starts the app.</summary>
    internal static bool TryRun(string[] args)
    {
        if (!TryParse(args, out var request)) return false;
        try
        {
            var settings = new NewPlusSettings(true, string.Empty, false, request.HideStartingDigits, request.ReplaceVariables, false);
            var created = NewPlusService.Create(request.Template, request.Folder, settings);
            NewPlusNative.NotifyCreated(created);
            // The rename thread is a background one: the process must not exit before it ends.
            NewPlusExplorer.BeginRename(created).Join(RenameWait);
        }
        catch (Exception ex)
        {
            // One message per process, so the format is parsed once here rather than cached.
            var format = CompositeFormat.Parse(Strings.NewPlus_CreateFailedFormat);
            MessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, format, Path.GetFileName(request.Template.TrimEnd('\\')), ex.Message),
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return true;
    }
}
