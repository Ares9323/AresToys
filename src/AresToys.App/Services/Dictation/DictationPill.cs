using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AresToys.App.Services.Dictation;

/// <summary>The small pill shown while dictating: a level meter while the microphone is open, then the phase
/// ("Transcribing…", the download of the model). It never takes the focus, so the text still goes where the user was.</summary>
internal sealed class DictationPill : Window
{
    private const int Bars = 5;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;

    private readonly Rectangle[] _bars = new Rectangle[Bars];
    private readonly TextBlock _text = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Func<float> _level;
    private readonly double[] _smoothed = new double[Bars];
    private int _tick;

    public DictationPill(Func<float> level)
    {
        _level = level;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var meter = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 22 };
        for (var i = 0; i < Bars; i++)
        {
            var bar = new Rectangle { Width = 4, Height = 4, RadiusX = 2, RadiusY = 2, Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Brushes.IndianRed };
            _bars[i] = bar;
            meter.Children.Add(bar);
        }

        _text.Margin = new Thickness(10, 0, 4, 0);
        _text.VerticalAlignment = VerticalAlignment.Center;
        _text.FontSize = 13;
        _text.SetResourceReference(TextBlock.ForegroundProperty, "AccentForegroundBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(meter);
        row.Children.Add(_text);

        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 8, 14, 8),
            BorderThickness = new Thickness(1),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.4 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "OuterBorderBrush");
        Content = new Grid { Margin = new Thickness(12), Children = { card } };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _ = SetWindowLongPtr(hwnd, GwlExStyle, GetWindowLongPtr(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate | WsExTransparent);
        };
        _timer.Tick += (_, _) => Animate();
    }

    /// <summary>Shows the meter with a caption.</summary>
    public void ShowListening(string caption)
    {
        _text.Text = caption;
        _meterActive = true;
        PlaceAndShow();
        _timer.Start();
    }

    /// <summary>Replaces the caption and stops the meter (the bars settle to dots).</summary>
    public void ShowPhase(string caption)
    {
        _text.Text = caption;
        _meterActive = false;
        if (!IsVisible) PlaceAndShow();
        Animate();
    }

    public void Dismiss()
    {
        _timer.Stop();
        Close();
    }

    private bool _meterActive;

    private void Animate()
    {
        _tick++;
        var level = _meterActive ? Math.Clamp(_level(), 0f, 1f) : 0f;
        for (var i = 0; i < Bars; i++)
        {
            // Each bar leans on the level with its own phase, so the meter ripples rather than moving as one block.
            var wave = _meterActive ? 0.55 + 0.45 * Math.Sin(_tick * 0.7 + i * 1.3) : 0.0;
            var target = 4 + level * wave * 18;
            _smoothed[i] += (target - _smoothed[i]) * 0.5;
            _bars[i].Height = Math.Max(4, _smoothed[i]);
        }
    }

    private void PlaceAndShow()
    {
        // Bottom centre of the monitor under the pointer, a little above the taskbar.
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        var work = GetMonitorInfo(monitor, ref info) ? info.rcWork : new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        var source = PresentationSource.FromVisual(Application.Current.MainWindow ?? this);
        var scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        Show();
        UpdateLayout();
        Left = (work.Left + (work.Right - work.Left) / 2.0) * scale - ActualWidth / 2;
        Top = (work.Bottom - 40) * scale - ActualHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int cbSize; public NativeRect rcMonitor; public NativeRect rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
