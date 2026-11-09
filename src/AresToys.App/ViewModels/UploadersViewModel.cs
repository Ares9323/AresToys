using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.App.Services.Plugins;
using AresToys.CustomUploaders;
using AresToys.PluginContracts;

namespace AresToys.App.ViewModels;

/// <summary>
/// Backs the Settings → Uploaders tab. Three categorized lists (Image / File / Text / Video) of
/// uploaders the user can multi-select via checkbox; selection persists through the
/// <see cref="PluginRegistry"/>'s settings-store-backed selection methods. Plus a read-only
/// listing of the loaded <c>.sxcu</c> custom uploaders so the user knows what's been imported.
/// </summary>
public sealed partial class UploadersViewModel : ObservableObject
{
    private readonly PluginRegistry _registry;

    public UploadersViewModel(PluginRegistry registry)
    {
        _registry = registry;
        ImageUploaders = [];
        FileUploaders  = [];
        TextUploaders  = [];
        VideoUploaders = [];
        UrlUploaders   = [];
        CustomUploaders = [];
        _ = LoadAsync();
        LoadCustomUploaders();
    }

    public ObservableCollection<UploaderSelectionItemViewModel> ImageUploaders { get; }
    public ObservableCollection<UploaderSelectionItemViewModel> FileUploaders  { get; }
    public ObservableCollection<UploaderSelectionItemViewModel> TextUploaders  { get; }
    public ObservableCollection<UploaderSelectionItemViewModel> VideoUploaders { get; }
    public ObservableCollection<UploaderSelectionItemViewModel> UrlUploaders   { get; }

    /// <summary>Read-only list of imported <c>.sxcu</c> files. Display-only: toggling them
    /// on/off goes through the per-category checkboxes above (custom uploaders surface there
    /// like any other plugin), this list just shows what's loaded + lets the user delete a
    /// file from disk.</summary>
    public ObservableCollection<CustomUploaderListItemViewModel> CustomUploaders { get; }

    public bool HasNoCustomUploaders => CustomUploaders.Count == 0;

    private async Task LoadAsync()
    {
        await PopulateAsync(UploaderCapabilities.Image, ImageUploaders).ConfigureAwait(true);
        await PopulateAsync(UploaderCapabilities.File,  FileUploaders).ConfigureAwait(true);
        await PopulateAsync(UploaderCapabilities.Text,  TextUploaders).ConfigureAwait(true);
        await PopulateAsync(UploaderCapabilities.Video, VideoUploaders).ConfigureAwait(true);
        await PopulateAsync(UploaderCapabilities.Url,   UrlUploaders).ConfigureAwait(true);
    }

    public void LoadCustomUploaders()
    {
        CustomUploaders.Clear();
        var folder = CustomUploaderRegistry.DefaultFolder;
        if (!System.IO.Directory.Exists(folder))
        {
            OnPropertyChanged(nameof(HasNoCustomUploaders));
            return;
        }
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.sxcu", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                var json = System.IO.File.ReadAllText(file);
                var config = CustomUploaderConfigLoader.Parse(json);
                if (!CustomUploaderConfigLoader.IsValid(config)) continue;
                var name = string.IsNullOrWhiteSpace(config!.Name) ? System.IO.Path.GetFileNameWithoutExtension(file) : config.Name!;
                var dest = string.IsNullOrWhiteSpace(config.DestinationType) ? "Any file" : config.DestinationType!;
                CustomUploaders.Add(new CustomUploaderListItemViewModel(name, dest, file));
            }
            catch { /* malformed file — skip silently, the registry already logged it at startup */ }
        }
        OnPropertyChanged(nameof(HasNoCustomUploaders));
    }

    private async Task PopulateAsync(UploaderCapabilities category, ObservableCollection<UploaderSelectionItemViewModel> target)
    {
        target.Clear();
        var selected = await _registry.GetSelectedIdsAsync(category, CancellationToken.None).ConfigureAwait(true);
        foreach (var uploader in _registry.AllUploaders)
        {
            if ((uploader.Capabilities & category) == 0) continue;
            var isSelected = selected.Contains(uploader.Id);
            target.Add(new UploaderSelectionItemViewModel(
                uploader, isSelected,
                (item, value) => _ = OnItemToggledAsync(category, target, item, value)));
        }
    }

    /// <summary>Re-read plugin enabled states + selections from the store. Called when the user
    /// switches to the Uploaders tab so changes made in Plugins (toggle on/off) are reflected.</summary>
    public Task ReloadAsync() => LoadAsync();

    private async Task OnItemToggledAsync(
        UploaderCapabilities category,
        ObservableCollection<UploaderSelectionItemViewModel> list,
        UploaderSelectionItemViewModel _,
        bool __)
    {
        // Persist the full ordered list of currently-selected ids for this category.
        var ids = list.Where(i => i.IsSelected).Select(i => i.Id).ToList();
        await _registry.SetSelectedIdsAsync(category, ids, CancellationToken.None).ConfigureAwait(true);
    }
}
