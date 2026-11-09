using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AresToys.App.Services;

namespace AresToys.App.Views;

/// <summary>What the lens and the label box show for one frame. The session builds it, the view
/// only draws it.</summary>
internal readonly record struct MagnifierFrame(
    BitmapSource? Lens,
    string? Hex,
    string? Rgb,
    string Coords,
    string Zoom,
    double CrosshairSize,
    MagnifierPlacement.Placement Placement);

/// <summary>The colour picker's window on ONE monitor. It paints that monitor's part of the
/// frozen snapshot, draws the lens and label box when the session says the cursor is on its
/// monitor, and forwards input to the session in physical screen pixels. It holds no picker
/// state and never places itself: <see cref="PerMonitorOverlayHost"/> does.</summary>
internal sealed partial class ScreenColorPickerView : Window
{
    private const double MagnifierBoxPx = 140;       // round display diameter in DIPs
    private readonly ScreenColorPickerOverlay _session;

    public ScreenColorPickerView(ScreenColorPickerOverlay session, OverlayMonitor monitor, BitmapSource background)
    {
        _session = session;
        Monitor = monitor;
        InitializeComponent();
        // The background is set before the window is ever shown: a first frame without it would
        // flash an empty window over the desktop.
        ScreenshotImage.Source = background;
    }

    public OverlayMonitor Monitor { get; }

    /// <summary>A physical screen pixel in this view's own DIP space (origin at its top-left).
    /// Only valid once the view is shown.</summary>
    public Point ToDips(int screenX, int screenY) => PointFromScreen(new Point(screenX, screenY));

    /// <summary>The work area of this view's monitor in this view's DIPs. The work area, not the
    /// full monitor rect, keeps the lens clear of the taskbar.</summary>
    public MagnifierPlacement.Rect WorkAreaInDips()
    {
        var wa = Monitor.WorkArea;
        var topLeft = ToDips(wa.X, wa.Y);
        var bottomRight = ToDips(wa.X + wa.Width, wa.Y + wa.Height);
        return new MagnifierPlacement.Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    /// <summary>Current size of the lens block and of the label box, with the design sizes as the
    /// fallback before the first layout pass.</summary>
    public (double CircleW, double CircleH, double LabelsW, double LabelsH) MagnifierSizes() => (
        MagnifierGroup.ActualWidth > 0 ? MagnifierGroup.ActualWidth : MagnifierBoxPx,
        MagnifierGroup.ActualHeight > 0 ? MagnifierGroup.ActualHeight : MagnifierBoxPx,
        MagnifierLabelsBorder.ActualWidth > 0 ? MagnifierLabelsBorder.ActualWidth : 130,
        MagnifierLabelsBorder.ActualHeight > 0 ? MagnifierLabelsBorder.ActualHeight : 92);

    public void ShowMagnifier(in MagnifierFrame frame)
    {
        if (frame.Lens is not null) MagnifierImage.Source = frame.Lens;
        if (frame.Hex is not null) HexLabel.Text = frame.Hex;
        if (frame.Rgb is not null) RgbLabel.Text = frame.Rgb;
        CoordsLabel.Text = frame.Coords;
        ZoomLabel.Text = frame.Zoom;
        MagnifierCrosshair.Width = frame.CrosshairSize;
        MagnifierCrosshair.Height = frame.CrosshairSize;

        Canvas.SetLeft(MagnifierGroup, frame.Placement.CircleX);
        Canvas.SetTop(MagnifierGroup, frame.Placement.CircleY);
        Canvas.SetLeft(MagnifierLabelsBorder, frame.Placement.LabelsX);
        Canvas.SetTop(MagnifierLabelsBorder, frame.Placement.LabelsY);
        MagnifierGroup.Visibility = Visibility.Visible;
        MagnifierLabelsBorder.Visibility = Visibility.Visible;
    }

    public void HideMagnifier()
    {
        MagnifierGroup.Visibility = Visibility.Hidden;
        MagnifierLabelsBorder.Visibility = Visibility.Hidden;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        _session.Commit((int)Math.Round(p.X), (int)Math.Round(p.Y));
        e.Handled = true;
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _session.Cancel();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _session.Zoom(e.Delta);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _session.Cancel();
        e.Handled = true;
    }
}
