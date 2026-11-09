using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Resources;
using AresToys.App.Services.NewPlus;
using AresToys.Storage.Paths;
using AresToys.Storage.Settings;
using Microsoft.Win32;

namespace AresToys.App.ViewModels;

/// <summary>Backs the Settings → New+ tab: the switch, the template folder and the PowerToys display
/// and behaviour options. Every change is saved at once and then pushed to the Explorer menu through
/// <see cref="NewPlusContextMenuSync.ApplyAsync"/>.</summary>
public sealed partial class NewPlusViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IStoragePathResolver _paths;
    private readonly NewPlusContextMenuSync _sync;
    private bool _loading;

    public NewPlusViewModel(ISettingsStore store, IStoragePathResolver paths, NewPlusContextMenuSync sync)
    {
        _store = store;
        _paths = paths;
        _sync = sync;
    }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _templateFolder = string.Empty;
    [ObservableProperty] private bool _hideFileExtension = true;
    [ObservableProperty] private bool _hideStartingDigits = true;
    [ObservableProperty] private bool _replaceVariables;
    [ObservableProperty] private bool _hideBuiltInNew;
    [ObservableProperty] private bool _duplicateWithCtrlD;
    [ObservableProperty] private string _errorText = string.Empty;

    public async Task LoadAsync()
    {
        var s = await NewPlusSettings.LoadAsync(_store, _paths, CancellationToken.None).ConfigureAwait(true);
        _loading = true;
        try
        {
            Enabled = s.Enabled;
            TemplateFolder = s.TemplateFolder;
            HideFileExtension = s.HideFileExtension;
            HideStartingDigits = s.HideStartingDigits;
            ReplaceVariables = s.ReplaceVariables;
            HideBuiltInNew = s.HideBuiltInNew;
            DuplicateWithCtrlD = s.DuplicateWithCtrlD;
        }
        finally { _loading = false; }
    }

    partial void OnEnabledChanged(bool value) => _ = PersistAsync();
    partial void OnTemplateFolderChanged(string value) => _ = PersistAsync();
    partial void OnHideFileExtensionChanged(bool value) => _ = PersistAsync();
    partial void OnHideStartingDigitsChanged(bool value) => _ = PersistAsync();
    partial void OnReplaceVariablesChanged(bool value) => _ = PersistAsync();
    partial void OnDuplicateWithCtrlDChanged(bool value) => _ = PersistAsync();

    partial void OnHideBuiltInNewChanged(bool value)
    {
        if (_loading) return;
        ErrorText = string.Empty;
        // The sync does this too, but it only logs; here a failure reaches the user.
        if (Enabled)
        {
            try { NewPlusContextMenuRegistration.SetBuiltInNewHidden(value); }
            catch { ErrorText = Strings.NewPlus_BuiltInError; }
        }
        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        if (_loading) return;
        try
        {
            var folder = string.IsNullOrWhiteSpace(TemplateFolder)
                ? NewPlusSettings.DefaultTemplateFolder(_paths)
                : TemplateFolder.Trim();
            await new NewPlusSettings(Enabled, folder, HideFileExtension, HideStartingDigits, ReplaceVariables, HideBuiltInNew, DuplicateWithCtrlD)
                .SaveAsync(_store, CancellationToken.None).ConfigureAwait(true);
            await _sync.ApplyAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Strings.NewPlus_PickFolderTitle,
            InitialDirectory = Directory.Exists(TemplateFolder) ? TemplateFolder : string.Empty,
        };
        if (dialog.ShowDialog() == true) TemplateFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(TemplateFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TemplateFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }
}
