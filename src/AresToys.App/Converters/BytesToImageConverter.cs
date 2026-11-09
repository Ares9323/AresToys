using System.Globalization;
using System.Windows.Data;
using AresToys.App.Services.ImageFiles;

namespace AresToys.App.Converters;

/// <summary>Decodes a byte[] (PNG/JPEG/WebP... bytes) into a frozen bitmap for image binding.
/// Returns null on null/empty input or decode failure: the bound Image just shows nothing.
/// WebP keeps its alpha channel (decoded through Skia, see <see cref="ImageDecoding"/>).</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is byte[] { Length: > 0 } bytes ? ImageDecoding.Decode(bytes) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
