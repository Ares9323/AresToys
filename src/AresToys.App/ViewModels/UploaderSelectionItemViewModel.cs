using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.PluginContracts;
using AresToys.Uploaders.OAuth;

namespace AresToys.App.ViewModels;

/// <summary>One uploader row inside a category section (Image / File / Text / Video) in
/// Settings → Uploaders. Toggling <see cref="IsSelected"/> updates the persisted selection list
/// for that category through the parent <see cref="UploadersViewModel"/>. <see cref="Uploader"/>
/// is exposed so the row's "Configure" button can open the per-uploader settings form for
/// anything that implements <see cref="IConfigurableUploader"/> or <see cref="IOAuthUploader"/>
/// (OAuth-only uploaders have no editable fields but still need the Sign in panel).</summary>
public sealed partial class UploaderSelectionItemViewModel : ObservableObject
{
    private readonly Action<UploaderSelectionItemViewModel, bool> _onSelectionChanged;

    public UploaderSelectionItemViewModel(
        IUploader uploader, bool initiallySelected,
        Action<UploaderSelectionItemViewModel, bool> onSelectionChanged)
    {
        Uploader = uploader;
        Id = uploader.Id;
        DisplayName = uploader.DisplayName;
        IsConfigurable = uploader is IConfigurableUploader or IOAuthUploader;
        _onSelectionChanged = onSelectionChanged;
        _isSelected = initiallySelected;
    }

    public IUploader Uploader { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public bool IsConfigurable { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged(this, value);
}
