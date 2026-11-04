using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AresToys.App.Views;

/// <summary>Where the recording controller sits inside the recorded area. No top right: it would
/// cover the recorded window's minimize/maximize/close buttons.</summary>
public enum RecordingControlsPosition { TopCenter, TopLeft, BottomLeft, BottomCenter, BottomRight }

public partial class RecordingOverlayWindow : Window
{
    private readonly DispatcherTimer _timer;
    private DateTime _resumedAt;
    private TimeSpan _accumulated = TimeSpan.Zero;
    private bool _paused;

    public RecordingOverlayWindow(int x, int y, int width, int height,
        RecordingControlsPosition position = RecordingControlsPosition.TopCenter)
    {
        InitializeComponent();
        // Rounded corners face inward, away from the screen edge the controller sits on.
        (Controller.HorizontalAlignment, Controller.VerticalAlignment, Controller.CornerRadius) = position switch
        {
            RecordingControlsPosition.TopLeft => (HorizontalAlignment.Left, VerticalAlignment.Top, new CornerRadius(0, 0, 4, 0)),
            RecordingControlsPosition.BottomLeft => (HorizontalAlignment.Left, VerticalAlignment.Bottom, new CornerRadius(0, 4, 0, 0)),
            RecordingControlsPosition.BottomCenter => (HorizontalAlignment.Center, VerticalAlignment.Bottom, new CornerRadius(4, 4, 0, 0)),
            RecordingControlsPosition.BottomRight => (HorizontalAlignment.Right, VerticalAlignment.Bottom, new CornerRadius(4, 0, 0, 0)),
            _ => (HorizontalAlignment.Center, VerticalAlignment.Top, new CornerRadius(0, 0, 4, 4)),
        };
        // (x,y,width,height) are physical pixels (Win32 capture region). Convert to DIPs once HwndSource exists.
        Left = x; Top = y; Width = width; Height = height;
        Loaded += (_, _) =>
        {
            var d = VisualTreeHelper.GetDpi(this);
            Left = x / d.DpiScaleX;
            Top = y / d.DpiScaleY;
            Width = width / d.DpiScaleX;
            Height = height / d.DpiScaleY;
        };

        // Exclude this overlay from screen capture so the timecode + Pause/Stop/Abort buttons
        // don't end up baked into the recording. WDA_EXCLUDEFROMCAPTURE (Win10 2004+) tells DWM
        // to skip the window in Graphics.Capture / DXGI / GDI capture paths used by ffmpeg's
        // gdigrab and the dshow desktop sources. WDA_MONITOR is the older fallback (it just
        // paints the window black in captures); we try the better one first and ignore failure.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                _ = SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
        };

        _resumedAt = DateTime.UtcNow;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var elapsed = _accumulated + (_paused ? TimeSpan.Zero : DateTime.UtcNow - _resumedAt);
            ElapsedLabel.Text = (_paused ? "PAUSED " : "REC ") + elapsed.ToString("mm\\:ss", System.Globalization.CultureInfo.InvariantCulture);
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public event EventHandler? StopRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ResumeRequested;
    public event EventHandler? AbortRequested;

    public void SetPausedVisual(bool paused)
    {
        if (paused == _paused) return;
        if (paused) _accumulated += DateTime.UtcNow - _resumedAt;
        else _resumedAt = DateTime.UtcNow;
        _paused = paused;
        PauseButton.Content = paused ? "▶ Resume" : "⏸ Pause";
    }

    private void OnStopClicked(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);
    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_paused) ResumeRequested?.Invoke(this, EventArgs.Empty);
        else PauseRequested?.Invoke(this, EventArgs.Empty);
    }
    private void OnAbortClicked(object sender, RoutedEventArgs e) => AbortRequested?.Invoke(this, EventArgs.Empty);

    private const uint WDA_MONITOR = 0x00000001;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
