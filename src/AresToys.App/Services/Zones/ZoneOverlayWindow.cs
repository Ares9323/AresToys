using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static AresToys.App.Services.Zones.ZoneNative;

namespace AresToys.App.Services.Zones;

/// <summary>Click through, topmost, non activating overlay that covers the work area of one monitor
/// and draws that monitor's zones, the highlighted one in the highlight color. The window is
/// positioned in device pixels with SetWindowPos; only the drawing is converted to WPF units, using
/// the DPI the window reports after it has been placed on its monitor. With zones spanning the monitors
/// the area is the combined rectangle of all of them and the window straddles several monitors: that
/// only happens when every monitor has the same DPI, so the one scale still converts every zone.</summary>
internal sealed class ZoneOverlayWindow : Window
{
    private readonly nint _hwnd;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false, UseLayoutRounding = true, SnapsToDevicePixels = true };
    private ZoneAppearance _appearance;
    private bool _showNumbers;
    private bool _shown;
    private PixelRect _drawnArea;
    private IReadOnlyList<PixelRect>? _drawnZones;
    private int[]? _drawnHighlight;
    private double _drawnScaleX;

    public ZoneOverlayWindow(ZoneAppearance appearance, bool showNumbers)
    {
        _appearance = appearance;
        _showNumbers = showNumbers;

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
        Content = _canvas;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(_hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate);

        // WPF only builds the visual tree once the window has been shown; show it off screen, then hide.
        Show();
        ShowWindow(_hwnd, SwHide);
    }

    /// <summary>Native handle, so the drag service can tell its own overlays from real windows.</summary>
    public nint Handle => _hwnd;

    public bool IsShown => _shown;

    /// <summary>Swaps in a new look and zone number setting; the next <see cref="Present"/> redraws.</summary>
    public void ApplyAppearance(ZoneAppearance appearance, bool showNumbers)
    {
        _appearance = appearance;
        _showNumbers = showNumbers;
        _drawnZones = null;
    }

    /// <summary>Shows the overlay over <paramref name="workArea"/> with the given zones (screen pixels)
    /// and every zone whose index is in <paramref name="highlighted"/> (empty for none) highlighted. Does
    /// nothing when the same picture is already on screen.</summary>
    public void Present(PixelRect workArea, IReadOnlyList<PixelRect> zones, IReadOnlyCollection<int> highlighted)
    {
        var set = highlighted.Order().ToArray();
        if (_shown && ReferenceEquals(zones, _drawnZones) && _drawnHighlight is not null
            && set.AsSpan().SequenceEqual(_drawnHighlight)
            && workArea == _drawnArea && Math.Abs(VisualTreeHelper.GetDpi(this).DpiScaleX - _drawnScaleX) < 0.001)
        {
            return;
        }

        Place(workArea);
        Draw(workArea, zones, set);
        _drawnArea = workArea;
        _drawnZones = zones;
        _drawnHighlight = set;
    }

    public void HideOverlay()
    {
        if (!_shown) return;
        ShowWindow(_hwnd, SwHide);
        _shown = false;
        _drawnZones = null;
    }

    /// <summary>Moves the window onto the work area (see <see cref="ZoneNative.PlaceTopmost"/>).</summary>
    private void Place(PixelRect area)
    {
        PlaceTopmost(_hwnd, area, !_shown);
        _shown = true;
    }

    private void Draw(PixelRect area, IReadOnlyList<PixelRect> zones, int[] highlighted)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        _drawnScaleX = scaleX;

        var normal = Frozen(ZoneAppearance.WithOpacity(_appearance.Fill, _appearance.FillOpacityPercent));
        var active = Frozen(ZoneAppearance.WithOpacity(_appearance.HighlightFill, _appearance.HighlightOpacityPercent));
        var border = Frozen(_appearance.Border);
        var thickness = new Thickness(_appearance.BorderThickness);
        var radius = new CornerRadius(_appearance.CornerRadius);
        var badgeBack = Frozen(Color.FromArgb(0xB0, 0x20, 0x20, 0x20));
        var badgeFore = Frozen(Colors.White);

        _canvas.Children.Clear();
        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];
            var box = new Border
            {
                Width = zone.Width / scaleX,
                Height = zone.Height / scaleY,
                Background = Array.BinarySearch(highlighted, i) >= 0 ? active : normal,
                BorderBrush = border,
                BorderThickness = thickness,
                CornerRadius = radius,
                IsHitTestVisible = false,
            };
            if (_showNumbers) box.Child = NumberBadge(i + 1, Math.Min(box.Width, box.Height), badgeBack, badgeFore);
            Canvas.SetLeft(box, (zone.Left - area.Left) / scaleX);
            Canvas.SetTop(box, (zone.Top - area.Top) / scaleY);
            _canvas.Children.Add(box);
        }
    }

    /// <summary>The 1 based zone number on a dark round badge, so it reads on any fill color. Sizes are DIPs and
    /// scale with the shorter side of the zone, within a readable range.</summary>
    private static Border NumberBadge(int number, double shorterSide, Brush back, Brush fore)
    {
        var fontSize = Math.Clamp(shorterSide * 0.3, 14, 96);
        var diameter = fontSize * 2;
        return new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            Background = back,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Foreground = fore,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            },
        };
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
