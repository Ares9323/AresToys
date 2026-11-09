using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AresToys.App.Services;
using AresToys.App.Services.Launcher;

namespace AresToys.App.Views;

/// <summary>Lists every visible top-level window with its title + process name + icon, lets
/// the user pick one, and returns the selection so the caller can populate WindowTitle /
/// ProcessName fields without typing them by hand. Same shortcut ShareX exposes in
/// Tools→Borderless Window for "remove the chrome from a running app".</summary>
public partial class WindowPickerDialog : Window
{
    private readonly List<OpenWindowInfo> _all;
    private readonly ObservableCollection<OpenWindowInfo> _filtered = [];

    public WindowPickerDialog(IconService icons)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _all = OpenWindowEnumerator.Enumerate(icons).ToList();
        foreach (var w in _all) _filtered.Add(w);
        WindowList.ItemsSource = _filtered;

        Loaded += (_, _) =>
        {
            FilterBox.Focus();
            // Pre-select the first row so OK / Enter has something to commit by default.
            if (WindowList.Items.Count > 0) WindowList.SelectedIndex = 0;
        };
    }

    /// <summary>The picked window — null if the user cancelled.</summary>
    public OpenWindowInfo? Result { get; private set; }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var needle = FilterBox.Text?.Trim() ?? string.Empty;
        _filtered.Clear();
        foreach (var w in _all)
        {
            if (string.IsNullOrEmpty(needle)
                || w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || w.ProcessName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                _filtered.Add(w);
            }
        }
        // Keep a usable selection: first row if the previous one fell out of view.
        if (WindowList.SelectedItem is null && _filtered.Count > 0) WindowList.SelectedIndex = 0;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void OnOkClicked(object sender, RoutedEventArgs e) => Commit();

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Commit()
    {
        if (WindowList.SelectedItem is not OpenWindowInfo info) return;
        Result = info;
        DialogResult = true;
        Close();
    }
}
