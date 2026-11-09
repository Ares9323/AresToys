using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.ImageEffects.Drawing;
using SkiaSharp;
// System.Windows.Media also defines a GradientStop type; alias the domain one so we don't
// have to fully-qualify every use site below.
using GradientStop = AresToys.ImageEffects.Drawing.GradientStop;

namespace AresToys.App.ViewModels.ImageEffects;

/// <summary>UI wrapper for a single <see cref="GradientStop"/>: exposes the colour as a WPF
/// brush (swatch in the list) and the location as 0..100 percent for the slider readout.
/// Changes are pushed back to the underlying domain object on every property mutation.</summary>
public sealed partial class GradientStopViewModel : ObservableObject
{
    public GradientStop Stop { get; }
    public Action? Changed { get; set; }

    public GradientStopViewModel(GradientStop stop)
    {
        Stop = stop;
        _location = stop.Location;
        _colorValue = stop.Color;
    }

    [ObservableProperty]
    private float _location;

    [ObservableProperty]
    private SKColor _colorValue;

    public Brush ColorBrush => Build(ColorValue);

    private static Brush Build(SKColor c)
    {
        var b = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        b.Freeze();
        return b;
    }

    partial void OnLocationChanged(float value)
    {
        Stop.Location = Math.Clamp(value, 0f, 100f);
        Changed?.Invoke();
    }

    partial void OnColorValueChanged(SKColor value)
    {
        Stop.Color = value;
        OnPropertyChanged(nameof(ColorBrush));
        Changed?.Invoke();
    }
}
