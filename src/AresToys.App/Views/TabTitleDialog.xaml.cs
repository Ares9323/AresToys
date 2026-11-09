using System.Windows;
using AresToys.App.Services;

namespace AresToys.App.Views;

/// <summary>Tiny modal for renaming a launcher tab. Keeps the rename flow obvious — a single
/// text box, OK/Cancel — so the user doesn't have to dig into Settings to give "tab 1" a
/// name like "Software".</summary>
public partial class TabTitleDialog : Window
{
    public TabTitleDialog(string tabKey, string current)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        HeaderText.Text = $"Rename tab  {tabKey}";
        TitleBox.Text = current;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>Renamed to TabTitle so it doesn't shadow Window.Title.</summary>
    public string TabTitle { get; private set; } = string.Empty;

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        TabTitle = TitleBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
