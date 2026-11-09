using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AresToys.Editor.Model;

namespace AresToys.Editor.Views;

/// <summary>Saturation × Value square paired with an external Hue control. X = saturation
/// (0 left → 1 right), Y = value (1 top → 0 bottom). Re-rasterises when H changes — cheap
/// (~few ms) so it's fine to do it on every Hue tick. Shares the H/S/V dependency-property
/// contract with <see cref="ColorWheelControl"/> so the host can swap one for the other
/// without rewiring state.</summary>
public partial class ColorSquareControl : UserControl
{
    /// <summary>Source bitmap pixel size — same as the wheel for visual parity. Scaled to
    /// fit the control via Image Stretch="Fill".</summary>
    private const int BitmapSize = 360;

    private WriteableBitmap? _bitmap;

    public Func<byte, byte, byte, (byte R, byte G, byte B)>? PixelGamma
    {
        get => _pixelGamma;
        set
        {
            _pixelGamma = value;
            if (_bitmap is not null) RenderSquare();
        }
    }
    private Func<byte, byte, byte, (byte R, byte G, byte B)>? _pixelGamma;

    public ColorSquareControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            EnsureRendered();
            UpdateCursor();
        };
        SizeChanged += (_, _) => UpdateCursor();
    }

    public static readonly DependencyProperty HProperty = DependencyProperty.Register(
        nameof(H), typeof(double), typeof(ColorSquareControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ColorSquareControl)d).OnHueChanged()));

    public static readonly DependencyProperty SProperty = DependencyProperty.Register(
        nameof(S), typeof(double), typeof(ColorSquareControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ColorSquareControl)d).UpdateCursor()));

    public static readonly DependencyProperty VProperty = DependencyProperty.Register(
        nameof(V), typeof(double), typeof(ColorSquareControl),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ColorSquareControl)d).UpdateCursor()));

    public double H { get => (double)GetValue(HProperty); set => SetValue(HProperty, value); }
    public double S { get => (double)GetValue(SProperty); set => SetValue(SProperty, value); }
    public double V { get => (double)GetValue(VProperty); set => SetValue(VProperty, value); }

    /// <summary>Fired on every mouse-driven update of <see cref="S"/>/<see cref="V"/>. Hosts that
    /// don't bind two-way can listen and recompute every other channel display.</summary>
    public event EventHandler? PointPicked;

    private void OnHueChanged()
    {
        if (_bitmap is not null) RenderSquare();
    }

    private void EnsureRendered()
    {
        if (_bitmap is not null) return;
        _bitmap = new WriteableBitmap(BitmapSize, BitmapSize, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
        SquareImage.Source = _bitmap;
        RenderSquare();
    }

    private void RenderSquare()
    {
        if (_bitmap is null) return;
        const int w = BitmapSize, h = BitmapSize;
        var hue = H;
        var pixels = new uint[w * h];
        for (var y = 0; y < h; y++)
        {
            var v = 1.0 - (double)y / (h - 1);
            for (var x = 0; x < w; x++)
            {
                var s = (double)x / (w - 1);
                var (r, g, b) = new Hsv(hue, s, v).ToRgb();
                if (_pixelGamma is not null) (r, g, b) = _pixelGamma(r, g, b);
                pixels[y * w + x] = ((uint)0xFF << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
            }
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    private void UpdateCursor()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        // Square is laid out edge-to-edge inside a 3px Border. Effective interior is the full
        // control bounds minus the border thickness on each side, so cursor sits exactly on the
        // colour pixels rather than over the frame.
        const double border = 3.0;
        var w = ActualWidth - 2 * border;
        var h = ActualHeight - 2 * border;
        if (w <= 0 || h <= 0) return;
        var px = border + S * w;
        var py = border + (1.0 - V) * h;
        Canvas.SetLeft(SquareCursor, px - SquareCursor.Width / 2);
        Canvas.SetTop(SquareCursor, py - SquareCursor.Height / 2);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Root.CaptureMouse();
        UpdateFromMouse(e.GetPosition(Root));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !Root.IsMouseCaptured) return;
        UpdateFromMouse(e.GetPosition(Root));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => Root.ReleaseMouseCapture();

    /// <summary>Map mouse pos → (S, V). Clamps both into [0,1] so dragging outside the square
    /// snaps to the edge; H is unaffected (the host owns the hue slider).</summary>
    private void UpdateFromMouse(Point p)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        const double border = 3.0;
        var w = ActualWidth - 2 * border;
        var h = ActualHeight - 2 * border;
        if (w <= 0 || h <= 0) return;
        S = Math.Clamp((p.X - border) / w, 0, 1);
        V = Math.Clamp(1.0 - (p.Y - border) / h, 0, 1);
        PointPicked?.Invoke(this, EventArgs.Empty);
    }
}
