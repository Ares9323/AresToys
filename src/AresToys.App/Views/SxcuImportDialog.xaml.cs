using System.IO;
using System.Windows;
using AresToys.App.Services;
using AresToys.CustomUploaders;

namespace AresToys.App.Views;

/// <summary>Modal that confirms a .sxcu import — invoked when AresToys.exe is launched with a
/// .sxcu file path (file association from Explorer, drag-and-drop, etc.). Mirrors ShareX's
/// "Custom uploader confirmation" prompt: shows the uploader name + category parsed from the
/// JSON, asks for explicit user consent before copying anything to disk. Cancelling closes
/// the dialog without touching the filesystem.</summary>
public partial class SxcuImportDialog : Window
{
    /// <summary>Source path of the .sxcu file the user double-clicked. Read on Install to
    /// copy into the custom-uploaders folder; never modified.</summary>
    private readonly string _sourcePath;

    public SxcuImportDialog(string sourcePath, CustomUploaderConfig config)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(config);
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _sourcePath = sourcePath;
        NameRun.Text = string.IsNullOrWhiteSpace(config.Name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : config.Name!;
        CategoryRun.Text = FriendlyCategory(config.DestinationType);
        SourcePathRun.Text = $"Source: {sourcePath}";
    }

    /// <summary>Absolute path the .sxcu was copied to on a successful install. Null when the
    /// user cancelled or the copy failed.</summary>
    public string? InstalledPath { get; private set; }

    private void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = CustomUploaderRegistry.DefaultFolder;
            Directory.CreateDirectory(folder);
            // Preserve the original filename to keep BuildStableId deterministic across runs
            // (id derives from path); collide-safe via "(2).sxcu" suffix when needed.
            var dest = Path.Combine(folder, Path.GetFileName(_sourcePath));
            if (File.Exists(dest))
            {
                var stem = Path.GetFileNameWithoutExtension(_sourcePath);
                var ext = Path.GetExtension(_sourcePath);
                for (var i = 2; ; i++)
                {
                    dest = Path.Combine(folder, $"{stem} ({i}){ext}");
                    if (!File.Exists(dest)) break;
                }
            }
            File.Copy(_sourcePath, dest);
            InstalledPath = dest;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't install the .sxcu file: {ex.Message}",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string FriendlyCategory(string? destinationType) => (destinationType?.Trim().ToLowerInvariant()) switch
    {
        "imageuploader"     => "Image",
        "fileuploader"      => "File",
        "textuploader"      => "Text",
        "urlshortener"      => "Text (URL shortener)",
        "urlsharingservice" => "Text (URL sharing)",
        _                   => "Any file",
    };
}
