using System.IO;
using System.Windows;
using AresToys.App.Services.Wormholes;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace AresToys.App.Views;

public partial class NewWormholeDialog : FluentWindow
{
    public NewWormholeDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
            // No more type selection — every wormhole is a folder mirror now. Auto-open the
            // folder picker as soon as the dialog appears (after the title field is focused)
            // so the user can pick a folder in one click. The title auto-fills from the folder
            // name if it's still the placeholder "Wormhole".
            Dispatcher.BeginInvoke(new Action(() => OnBrowseClicked(this, new RoutedEventArgs())),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    /// <summary>Result of an OK confirmation. Null if the dialog was cancelled. The caller hands
    /// this off to <see cref="IWormholeWindowManager"/> which materialises the record.</summary>
    public NewWormholeChoice? Result { get; private set; }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        // .NET 8+ ships Microsoft.Win32.OpenFolderDialog directly in WPF — no WinForms interop
        // needed and no third-party folder picker. Initial directory defaults to the user's
        // Desktop, matching Portals' "Default Folder Picker Folder" behaviour out of the box.
        var dlg = new OpenFolderDialog
        {
            Title = "Choose the folder this wormhole will mirror",
            InitialDirectory = string.IsNullOrWhiteSpace(FolderBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : FolderBox.Text,
        };
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
            // Auto-fill the title from the folder name when the user hasn't typed anything
            // custom yet (still on the default). Matches the Portals "Use Custom Display Name"
            // workflow — most users want the folder name as the title and this saves a
            // keystroke.
            if (TitleBox.Text == "Wormhole")
                TitleBox.Text = Path.GetFileName(dlg.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        var title = (TitleBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(title))
        {
            System.Windows.MessageBox.Show(this, "Title can't be empty.", "AresToys",
                System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            TitleBox.Focus();
            return;
        }

        var folder = (FolderBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            System.Windows.MessageBox.Show(this, "Pick an existing folder for this wormhole to mirror.",
                "AresToys", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            FolderBox.Focus();
            return;
        }
        Result = new NewWormholeChoice(title, folder);

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>Captures the user's choice from <see cref="NewWormholeDialog"/>. The manager turns
/// this into a persisted <see cref="WormholeRecord"/> + a live window.</summary>
public sealed record NewWormholeChoice(string Title, string SourceFolder);
