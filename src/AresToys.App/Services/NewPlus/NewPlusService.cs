using System.IO;
using AresToys.Storage.Paths;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.NewPlus;

/// <summary>Turns a template into a file or folder in the Explorer folder the menu was opened in.
/// Runs in the primary instance: the Explorer verb starts <c>arestoys.exe --newplus</c>, the secondary
/// forwards the template and the folder over the single instance pipe (<see cref="SingleInstanceGuard.NewPlusPrefix"/>).</summary>
public sealed class NewPlusService
{
    private readonly ISettingsStore _store;
    private readonly IStoragePathResolver _paths;
    private readonly ILogger<NewPlusService> _logger;

    public NewPlusService(ISettingsStore store, IStoragePathResolver paths, ILogger<NewPlusService> logger)
    {
        _store = store;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Copies <paramref name="templatePath"/> into <paramref name="targetFolder"/>, resolves
    /// the variables when the option is on, and leaves the result in rename mode. Never throws:
    /// a failure is logged.</summary>
    public async Task CreateAsync(string templatePath, string targetFolder)
    {
        try
        {
            var settings = await NewPlusSettings.LoadAsync(_store, _paths, CancellationToken.None).ConfigureAwait(false);
            var created = await Task.Run(() => Create(templatePath, targetFolder, settings)).ConfigureAwait(false);
            NewPlusNative.NotifyCreated(created);
            NewPlusExplorer.BeginRename(created);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New+ could not create {Template} in {Folder}", templatePath, targetFolder);
        }
    }

    /// <summary>The copy itself, without the Explorer niceties. Returns the path that was created.</summary>
    internal static string Create(string templatePath, string targetFolder, NewPlusSettings settings)
    {
        templatePath = templatePath.TrimEnd('\\');
        if (!File.Exists(templatePath) && !Directory.Exists(templatePath))
            throw new FileNotFoundException("The template no longer exists.", templatePath);
        if (!Directory.Exists(targetFolder))
            throw new DirectoryNotFoundException($"The folder does not exist: {targetFolder}");

        var now = DateTime.Now;
        var name = NewPlusTemplates.TargetName(templatePath, settings.HideStartingDigits);
        if (settings.ReplaceVariables)
            name = NewPlusTemplates.ResolveFileName(name, Path.GetFileName(targetFolder.TrimEnd('\\')), now);

        var target = NewPlusTemplates.MakeUniquePath(Path.Combine(targetFolder, name));
        NewPlusNative.CopyWithShell(templatePath, target);

        if (settings.ReplaceVariables && Directory.Exists(target))
            ResolveInside(target, Path.GetFileName(target), now);
        Touch(target, now);
        return target;
    }

    // Renames what is inside a created folder. Children first, each resolved against the NEW name of
    // its folder, so $PARENT_FOLDER_NAME inside "$YYYY" reads the year and not the placeholder.
    private static void ResolveInside(string folder, string folderName, DateTime now)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
        {
            if (NewPlusTemplates.IsHiddenOrSystem(entry)) continue;
            var name = Path.GetFileName(entry);
            var resolved = NewPlusTemplates.ResolveFileName(name, folderName, now);
            if (Directory.Exists(entry)) ResolveInside(entry, resolved, now);
            if (string.Equals(name, resolved, StringComparison.OrdinalIgnoreCase)) continue;
            var destination = NewPlusTemplates.MakeUniquePath(Path.Combine(folder, resolved));
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
    }

    // A new file made from a template should sort as new: the copy keeps the template's old times.
    private static void Touch(string path, DateTime now)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                    SetTime(entry, now);
            }
            SetTime(path, now);
        }
        catch { /* the timestamps are cosmetic */ }
    }

    private static void SetTime(string path, DateTime now)
    {
        if (Directory.Exists(path)) Directory.SetLastWriteTime(path, now);
        else File.SetLastWriteTime(path, now);
    }
}
