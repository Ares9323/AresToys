using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace AresToys.App.Converters;

/// <summary>MultiBinding: [0] a file path, [1] the <see cref="AresToys.App.Services.Launcher.IconService"/>; returns
/// the file's shell icon at 32 px (null when the path is empty or has no icon). Lets an item template show an exe's
/// icon without the item type knowing about icons.</summary>
public sealed class PathIconConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length >= 2 && values[0] is string { Length: > 0 } path && values[1] is AresToys.App.Services.Launcher.IconService icons
            ? icons.GetIconAtSize(path, 32)
            : null;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToAppearanceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ControlAppearance.Primary : ControlAppearance.Secondary;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Translates an enum value to its localised display label via the same `EnumValue_<Name>`
/// resx lookup the property-grid combos use. ConvertBack is a no-op since DisplayMemberPath /
/// item-template usage is one-way (the SelectedItem stays the raw enum value).</summary>
public sealed class EnumValueDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        var raw = value.ToString() ?? string.Empty;
        return Services.ImageEffectLocalizer.LocalizeEnumValue(raw, raw);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    // DependencyProperty.UnsetValue lands here when a binding path traversal fails (e.g.
    // SelectedEntry.SideToggles where SelectedEntry is itself null). It's a sentinel, NOT
    // null — so a naive "value is null" check would treat it as a real value and render the
    // bound element. Treat UnsetValue as null to keep "no source" hidden.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value == DependencyProperty.UnsetValue ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value == DependencyProperty.UnsetValue ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>String null OR empty → Visible, anything with content → Collapsed. Used for the
/// "empty state" placeholder shown inside an input when the bound text is missing — paired
/// with NonEmptyToVisibility on the live content TextBlock so exactly one of the two is shown.</summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null
            || value == DependencyProperty.UnsetValue
            || (value is string s && s.Length == 0)
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True → 0.4 (dimmed), false → 1.0 (full). Used to fade the source row while it's being
/// dragged so the user sees what they picked up.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.4 : 1.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? false : true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? false : true;
}

/// <summary>0 → Visible, anything else → Collapsed. Used for "(none yet)" placeholders bound
/// to a collection's <c>Count</c>.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True → Collapsed, false → Visible. Used to hide a region when a flag is on
/// (e.g. show the list view only when *not* editing).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Wraps a <see cref="System.Windows.Media.Color"/> in a frozen
/// <see cref="System.Windows.Media.SolidColorBrush"/> so XAML can bind a Background /
/// Fill directly off a Color-typed property (e.g. an ObservableCollection&lt;Color&gt;
/// rendered via ItemsControl). Returns Transparent for null / non-Color inputs so the
/// binding doesn't blow up during template materialisation.</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Windows.Media.Color c)
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        return System.Windows.Media.Brushes.Transparent;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
