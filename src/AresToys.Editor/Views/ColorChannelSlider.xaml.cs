using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AresToys.Editor.Views;

/// <summary>Horizontal click-and-drag slider whose track is filled with a caller-supplied gradient
/// brush. Used by <see cref="ColorPickerWindow"/> to give every channel slider (R/G/B/H/S/V/A) a
/// preview of what dragging the thumb will produce — Unreal-style "see the gradient before you
/// touch it". Caller updates <see cref="TrackBrush"/> dynamically (e.g. when other channels
/// change) to keep the preview accurate.</summary>
public partial class ColorChannelSlider : UserControl
{
    public ColorChannelSlider()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateThumbPosition();
        Loaded += (_, _) => UpdateThumbPosition();
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ColorChannelSlider),
        new PropertyMetadata(0.0, (d, _) => ((ColorChannelSlider)d).UpdateThumbPosition()));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ColorChannelSlider),
        new PropertyMetadata(255.0, (d, _) => ((ColorChannelSlider)d).UpdateThumbPosition()));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ColorChannelSlider),
        new FrameworkPropertyMetadata(
            0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((ColorChannelSlider)d).OnValueChanged((double)e.NewValue)));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ColorChannelSlider),
        new PropertyMetadata(Brushes.Transparent, (d, e) =>
        {
            var s = (ColorChannelSlider)d;
            s.TrackBorder.Background = (Brush)e.NewValue;
        }));

    /// <summary>Toggles the checker-pattern background behind the gradient — used only by the
    /// alpha slider so the user can see how transparent the colour really is.</summary>
    public static readonly DependencyProperty ShowCheckerProperty = DependencyProperty.Register(
        nameof(ShowChecker), typeof(bool), typeof(ColorChannelSlider),
        new PropertyMetadata(false, (d, e) =>
        {
            var s = (ColorChannelSlider)d;
            s.CheckerBorder.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            if ((bool)e.NewValue && s.CheckerBorder.Background is null)
            {
                s.CheckerBorder.Background = (Brush)Application.Current.Resources["CheckerBrush"];
                s.CheckerBorder.BorderBrush = (Brush)new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                s.CheckerBorder.BorderThickness = new Thickness(1);
                s.CheckerBorder.CornerRadius = new CornerRadius(3);
            }
        }));

    public double Minimum   { get => (double)GetValue(MinimumProperty);   set => SetValue(MinimumProperty, value); }
    public double Maximum   { get => (double)GetValue(MaximumProperty);   set => SetValue(MaximumProperty, value); }
    public double Value     { get => (double)GetValue(ValueProperty);     set => SetValue(ValueProperty, value); }
    public Brush  TrackBrush{ get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public bool   ShowChecker{ get => (bool)GetValue(ShowCheckerProperty);set => SetValue(ShowCheckerProperty, value); }

    /// <summary>Fired alongside <see cref="ValueProperty"/> changes so callers that don't bind can
    /// react with code (e.g. recompute other channels' track gradients).</summary>
    public event EventHandler<double>? ValueChanged;

    private void OnValueChanged(double newValue)
    {
        UpdateThumbPosition();
        ValueChanged?.Invoke(this, newValue);
    }

    private void UpdateThumbPosition()
    {
        var span = Maximum - Minimum;
        if (span <= 0 || ActualWidth <= 0) return;
        var t = Math.Clamp((Value - Minimum) / span, 0, 1);
        // Centre the thumb's 6-pixel width on the value point. Clamping at the edges so the thumb
        // doesn't disappear off the track when Value==Min/Max.
        Canvas.SetLeft(Thumb, Math.Clamp(t * ActualWidth - Thumb.Width / 2, 0, ActualWidth - Thumb.Width));
    }

    private void OnTrackMouseDown(object sender, MouseButtonEventArgs e)
    {
        TrackBorder.CaptureMouse();
        UpdateValueFromMouse(e.GetPosition(TrackBorder));
    }

    private void OnTrackMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !TrackBorder.IsMouseCaptured) return;
        UpdateValueFromMouse(e.GetPosition(TrackBorder));
    }

    private void OnTrackMouseUp(object sender, MouseButtonEventArgs e) => TrackBorder.ReleaseMouseCapture();

    private void UpdateValueFromMouse(Point p)
    {
        if (TrackBorder.ActualWidth <= 0) return;
        var t = Math.Clamp(p.X / TrackBorder.ActualWidth, 0, 1);
        Value = Minimum + t * (Maximum - Minimum);
    }
}
