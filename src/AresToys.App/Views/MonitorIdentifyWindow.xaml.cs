using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AresToys.App.Services.Zones;
using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Views;

/// <summary>What one monitor's identify window shows: the monitor, its number, and the name of its layout.</summary>
internal sealed record MonitorIdentity(ZoneMonitor Monitor, int Number, string LayoutText);

/// <summary>Click through, topmost, non activating window centered on a monitor's work area that shows the
/// monitor number in very large text, the resolution under it and the assigned layout under that. Placed in
/// device pixels (see <see cref="ZoneNative.PlaceTopmost"/>); only the content is sized in WPF units, from the
/// DPI the window reports once it sits on its monitor.</summary>
internal sealed class MonitorIdentifyWindow : Window
{
    private const double NumberHeightFraction = 0.25;

    public MonitorIdentifyWindow(MonitorIdentity identity)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate);

        Show();
        PlaceTopmost(hwnd, identity.Monitor.WorkArea, false);

        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double numberSize = identity.Monitor.WorkArea.Height / scale * NumberHeightFraction;
        Content = BuildContent(identity, numberSize);
    }

    private static Border BuildContent(MonitorIdentity identity, double numberSize)
    {
        var area = identity.Monitor.WorkArea;
        var white = Brushes.White;
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(Line(identity.Number.ToString(System.Globalization.CultureInfo.CurrentCulture), numberSize, FontWeights.Bold, white));
        panel.Children.Add(Line($"{area.Width} x {area.Height}", Math.Max(14, numberSize * 0.2), FontWeights.SemiBold, white));
        panel.Children.Add(Line(identity.LayoutText, Math.Max(12, numberSize * 0.14), FontWeights.Normal, white));

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x14, 0x14)),
            CornerRadius = new CornerRadius(numberSize * 0.12),
            Padding = new Thickness(numberSize * 0.3, numberSize * 0.08, numberSize * 0.3, numberSize * 0.16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = panel,
        };
    }

    private static TextBlock Line(string text, double size, FontWeight weight, Brush brush) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };
}

/// <summary>Shows one <see cref="MonitorIdentifyWindow"/> per monitor for a short time. Showing again while
/// they are up replaces them and restarts the timer, so windows never stack. Dispose closes everything.</summary>
internal sealed class MonitorIdentifier : IDisposable
{
    private const int ShowMilliseconds = 2500;

    private readonly List<MonitorIdentifyWindow> _windows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(ShowMilliseconds) };

    public MonitorIdentifier() => _timer.Tick += (_, _) => Close();

    public void Show(IEnumerable<MonitorIdentity> identities)
    {
        Close();
        foreach (var identity in identities)
        {
            _windows.Add(new MonitorIdentifyWindow(identity));
        }

        _timer.Start();
    }

    public void Dispose() => Close();

    private void Close()
    {
        _timer.Stop();
        foreach (var window in _windows)
        {
            window.Close();
        }

        _windows.Clear();
    }
}
