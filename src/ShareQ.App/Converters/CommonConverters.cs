using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace ShareQ.App.Converters;

public sealed class BoolToAppearanceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ControlAppearance.Primary : ControlAppearance.Secondary;
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
