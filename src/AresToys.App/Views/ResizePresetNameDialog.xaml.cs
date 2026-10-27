using System.Globalization;
using System.Windows;
using AresToys.App.Resources;
using Wpf.Ui.Controls;

namespace AresToys.App.Views;

/// <summary>Asks for the name of a new resize preset. <see cref="PresetName"/> is null when the
/// prompt was cancelled.</summary>
public partial class ResizePresetNameDialog : FluentWindow
{
    public ResizePresetNameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string? PresetName { get; private set; }

    private static string Loc(string key)
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        return Strings.ResourceManager.GetString(key, culture) ?? key;
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            System.Windows.MessageBox.Show(this, Loc("ResizeQuick_NameRequired"), Loc("ResizeQuick_SaveAsPreset"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }
        PresetName = name;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
}
