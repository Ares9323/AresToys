using System.Windows;
using System.Windows.Controls;
using AresToys.App.Services;
using AresToys.App.ViewModels;

namespace AresToys.App.Views;

/// <summary>Modal grid of FontAwesome icons. Click an icon → <see cref="PickedGlyph"/> holds the
/// codepoint string + DialogResult=true. Cancel → DialogResult=false. Clear → DialogResult=true
/// with empty <see cref="PickedGlyph"/> (caller treats that as "remove icon"). Dialog is opened
/// via <see cref="ShowDialog"/> like any other WPF modal — caller reads the property after the
/// blocking call returns.</summary>
public partial class IconPickerDialog : Window
{
    public IconPickerDialog(string? currentGlyph = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        // Initial grid = full catalog. Filter narrows it down on every TextChanged tick.
        IconGrid.ItemsSource = IconCatalog.All;
        // Focus the search box on open so the user can start typing right away — keeps the
        // mouse-free flow consistent with the rest of the app's pickers.
        Loaded += (_, _) => SearchBox.Focus();
        // currentGlyph is taken just to satisfy a future "highlight the active selection"
        // pass — for now the dialog always opens with the grid neutral.
        _ = currentGlyph;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.TextBox tb) return;
        var query = tb.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            IconGrid.ItemsSource = IconCatalog.All;
            return;
        }
        // Case-insensitive substring match against the FontAwesome slug. Cheap enough on
        // ~200 entries to do on every keystroke; no debounce needed.
        IconGrid.ItemsSource = IconCatalog.All
            .Where(i => i.Name.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The chosen glyph (FontAwesome codepoint as a string) when DialogResult is true.
    /// Empty string means the user clicked Clear.</summary>
    public string PickedGlyph { get; private set; } = string.Empty;

    private void OnIconClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        PickedGlyph = btn.Tag as string ?? string.Empty;
        DialogResult = true;
        Close();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        PickedGlyph = string.Empty;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
