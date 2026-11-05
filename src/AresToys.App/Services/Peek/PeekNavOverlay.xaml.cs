using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AresToys.App.Services.Peek;

/// <summary>Where the preview area of Peek is on screen, in physical pixels, and the matrix that turns pixels into DIPs.</summary>
public readonly record struct PeekNavArea(int X, int Y, int Width, int Height, Matrix FromDevice);

/// <summary>The previous and next chevrons of Peek. A native child (web view, preview handler) paints over every WPF
/// element of the window, so the chevrons live in this small owned window that covers the preview area and sits above it.
/// It never takes the focus. A timer reads the cursor and fades a chevron in while the cursor is within
/// <see cref="EdgeDips"/> of the matching edge of the area, and out when it leaves.</summary>
public partial class PeekNavOverlay : Window
{
    private const double EdgeDips = 96;
    private const int FadeMs = 120;
    private const uint Swp_NoActivate = 0x0010;
    private const uint Swp_ShowWindow = 0x0040;
    // Owned by Peek, so it already sits right above it; topmost would float the chevrons over other apps covering Peek.
    private const uint Swp_NoZOrder = 0x0004;

    private readonly Func<PeekNavArea?> _area;
    private readonly DispatcherTimer _timer;
    private bool _hasPrevious;
    private bool _hasNext;
    private bool _running;
    private bool _prevShown;
    private bool _nextShown;
    private PeekNavArea _last;

    public event Action? PreviousClicked;

    public event Action? NextClicked;

    /// <param name="owner">The Peek window; the overlay stays above it and goes with it.</param>
    /// <param name="area">Gives the preview area now, or null while Peek is hidden or minimised.</param>
    public PeekNavOverlay(Window owner, Func<PeekNavArea?> area)
    {
        InitializeComponent();
        Owner = owner;
        _area = area;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, -20);
        SetWindowLongPtr(hwnd, -20, ex | 0x08000000 | 0x00000080);
    }

    /// <summary>Starts following the cursor and the window. Call while Peek is visible.</summary>
    public void Start()
    {
        _running = true;
        _timer.Start();
        Tick();
    }

    /// <summary>Stops the timer and hides the overlay (Peek hides, or goes away).</summary>
    public void Stop()
    {
        _running = false;
        _timer.Stop();
        SetShown(PrevButton, ref _prevShown, false, immediate: true);
        SetShown(NextButton, ref _nextShown, false, immediate: true);
        if (IsVisible) Hide();
    }

    /// <summary>Which chevrons have a file to go to.</summary>
    public void SetAvailability(bool hasPrevious, bool hasNext)
    {
        _hasPrevious = hasPrevious;
        _hasNext = hasNext;
        if (_running) Tick();
    }

    /// <summary>Follows the preview area right away (Peek moved or resized).</summary>
    public void Sync()
    {
        if (_running) Tick();
    }

    private void Tick()
    {
        if (!_running) return;
        if (_area() is not { } area || area.Width <= 0 || area.Height <= 0)
        {
            if (IsVisible) Hide();
            return;
        }

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible)
        {
            Show();
            _last = default;
        }

        if (area.X != _last.X || area.Y != _last.Y || area.Width != _last.Width || area.Height != _last.Height)
        {
            SetWindowPos(hwnd, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, Swp_NoActivate | Swp_ShowWindow | Swp_NoZOrder);
            _last = area;
        }

        var left = false;
        var right = false;
        if (GetCursorPos(out var cursor)
            && cursor.X >= area.X && cursor.X < area.X + area.Width
            && cursor.Y >= area.Y && cursor.Y < area.Y + area.Height)
        {
            // The edge distance is a size in DIPs: bring the cursor offsets from pixels to DIPs first.
            var offset = area.FromDevice.Transform(new Point(cursor.X - area.X, cursor.Y - area.Y));
            var widthDips = area.FromDevice.Transform(new Point(area.Width, 0)).X;
            left = offset.X <= EdgeDips;
            right = offset.X >= widthDips - EdgeDips;
        }

        SetShown(PrevButton, ref _prevShown, left && _hasPrevious, immediate: false);
        SetShown(NextButton, ref _nextShown, right && _hasNext, immediate: false);
    }

    private static void SetShown(Button button, ref bool state, bool shown, bool immediate)
    {
        if (state == shown && !immediate) return;
        state = shown;
        button.IsHitTestVisible = shown;
        if (immediate)
        {
            button.BeginAnimation(OpacityProperty, null);
            button.Opacity = shown ? 1 : 0;
            return;
        }

        button.BeginAnimation(OpacityProperty, new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(FadeMs)));
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => PreviousClicked?.Invoke();

    private void OnNextClick(object sender, RoutedEventArgs e) => NextClicked?.Invoke();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
