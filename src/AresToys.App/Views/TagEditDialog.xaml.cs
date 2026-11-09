using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AresToys.Storage.Items;
using Wpf.Ui.Controls;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AresToys.App.Views;

/// <summary>Create / edit one clipboard tag (issue #4): a name plus an optional colour from a
/// small palette. Used by the clipboard window's "Tags → New tag…" entry and by the tag editor
/// in Settings. <see cref="ResultName"/> is the normalised name on OK; <see cref="ResultColor"/>
/// is <c>#RRGGBB</c> or null for the neutral chip. The optional <c>isNameTaken</c> callback lets
/// the caller refuse a name another tag already uses without closing the dialog.</summary>
public partial class TagEditDialog : FluentWindow
{
    /// <summary>Preset swatches: distinct hues that stay readable on both dark and light
    /// surfaces. The chip text colour is picked per swatch for contrast (see TagBadge).</summary>
    private static readonly string[] Palette =
    [
        "#E5484D", "#F76B15", "#FFC53D", "#46A758", "#12A594",
        "#0090FF", "#3E63DD", "#8E4EC6", "#D6409F", "#8D8D8D",
    ];

    private readonly Func<string, bool>? _isNameTaken;

    public TagEditDialog(string title, string initialName = "", string? initialColor = null, Func<string, bool>? isNameTaken = null)
    {
        InitializeComponent();
        Title = title;
        _isNameTaken = isNameTaken;
        NameBox.Text = initialName;
        BuildSwatches(TagRules.NormalizeColor(initialColor));
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string? ResultName { get; private set; }
    public string? ResultColor { get; private set; }

    private void BuildSwatches(string? selected)
    {
        var neutral = new RadioButton
        {
            Style = (Style)FindResource("SwatchRadio"),
            Tag = null,
            ToolTip = Loc("TagDialog_NoColor"),
            IsChecked = selected is null,
            // Slash across the neutral swatch: reads as "none" at a glance.
            Content = new Line
            {
                X1 = 0, Y1 = 12, X2 = 12, Y2 = 0,
                StrokeThickness = 1.5,
                Stroke = (Brush)FindResource("AccentForegroundDarkBrush"),
            },
        };
        neutral.SetResourceReference(BackgroundProperty, "Surface1Brush");
        SwatchPanel.Children.Add(neutral);

        var colors = Palette.ToList();
        if (selected is not null && !colors.Contains(selected, StringComparer.OrdinalIgnoreCase)) colors.Add(selected);
        foreach (var hex in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            SwatchPanel.Children.Add(new RadioButton
            {
                Style = (Style)FindResource("SwatchRadio"),
                Tag = hex,
                ToolTip = hex,
                Background = brush,
                IsChecked = string.Equals(hex, selected, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private static string Loc(string key)
        => AresToys.App.Resources.Strings.ResourceManager.GetString(key,
               AresToys.App.Markup.LocalizedStrings.Instance.Culture ?? System.Globalization.CultureInfo.CurrentUICulture) ?? key;

    private void OnNameChanged(object sender, TextChangedEventArgs e)
        => ErrorText.Visibility = Visibility.Collapsed;

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        var name = TagRules.NormalizeName(NameBox.Text);
        if (name is null)
        {
            NameBox.Focus();
            return;
        }
        if (_isNameTaken?.Invoke(name) == true)
        {
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            NameBox.SelectAll();
            return;
        }
        ResultName = name;
        ResultColor = SwatchPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
