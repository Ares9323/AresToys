using CommunityToolkit.Mvvm.ComponentModel;

namespace AresToys.App.ViewModels;

/// <summary>One row in the Settings → Uploaders → Custom uploaders list. Display-only — toggling
/// a custom uploader on/off goes through the regular per-category checkboxes above; this row's
/// "Delete" button just removes the .sxcu file from disk (effective after restart).</summary>
public sealed partial class CustomUploaderListItemViewModel : ObservableObject
{
    public CustomUploaderListItemViewModel(string displayName, string destinationType, string filePath)
    {
        DisplayName = displayName;
        FilePath = filePath;
        Subtitle = $"{destinationType} · {System.IO.Path.GetFileName(filePath)}";
    }

    public string DisplayName { get; }
    public string Subtitle { get; }
    public string FilePath { get; }
}
