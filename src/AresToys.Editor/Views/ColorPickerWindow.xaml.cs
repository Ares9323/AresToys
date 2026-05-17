using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AresToys.Editor.Model;

namespace AresToys.Editor.Views;

/// <summary>ShareX-style HSB/RGB/CMYK colour picker with Unreal-inspired gradient sliders. Single
/// source of truth is HSV (<see cref="_h"/>/<see cref="_s"/>/<see cref="_v"/>) + alpha; every other
/// representation is computed from those on demand. <see cref="_suppress"/> guards every UI sync
/// pass against feedback loops (a slider firing ValueChanged → updating HSV → updating sliders →
/// firing ValueChanged again).</summary>
public partial class ColorPickerWindow : Wpf.Ui.Controls.FluentWindow
{
    // Authoritative state in HSV [0,1] + alpha byte. Everything else is derived.
    private double _h, _s, _v;
    private byte _a = 255;
    private bool _suppress;
    private readonly ShapeColor _originalColor;

    // ── UI preferences persisted across picker openings (same app session). Defaults match
    //    the XAML-default selections so the very first open is unchanged. Static-only:
    //    survives every dialog close/reopen but resets on app restart. Cross-restart
    //    persistence would need an ISettingsStore injection; not done yet on purpose —
    //    these are tiny UI prefs, in-session is plenty for the "don't make me re-toggle"
    //    annoyance the user flagged.
    private static bool _persistedSquareShape;   // false → Wheel
    private static bool _persistedCmykTab;       // false → RGB
    private static bool _persistedSrgbOff;       // false → sRGB ON (XAML default)

    public ColorPickerWindow(ShapeColor initial)
    {
        InitializeComponent();
        _originalColor = initial;
        var hsv = Hsv.FromRgb(initial.R, initial.G, initial.B);
        _h = hsv.H;
        _s = hsv.S;
        _v = hsv.V;
        _a = initial.A;

        WireSliderInputs();
        RestorePersistedPreferences();
        // Wheel pushes (H, S) back into our HSV state — same path used by the H/S text inputs
        // and sliders, so it goes through UpdateAllUi which keeps every other channel in sync.
        ColorWheel.PointPicked += (_, _) =>
        {
            if (_suppress) return;
            _h = ColorWheel.H;
            _s = ColorWheel.S;
            UpdateAllUi();
        };
        // Hand the wheel our gamma function so its disc bitmap follows the sRGB toggle the same
        // way the sliders / palette / New-preview do. Identity transform when toggle ON.
        ColorWheel.PixelGamma = ApplyPreviewGamma;
        // Square mirrors the wheel: same PointPicked → same UpdateAllUi pipeline, same gamma
        // hookup. The shape radios just flip Visibility — bindings keep state coherent.
        ColorSquare.PointPicked += (_, _) =>
        {
            if (_suppress) return;
            _h = ColorSquare.H;
            _s = ColorSquare.S;
            _v = ColorSquare.V;
            UpdateAllUi();
        };
        ColorSquare.PixelGamma = ApplyPreviewGamma;
        SizeChanged += (_, _) => UpdateAllUi();
        Loaded += (_, _) =>
        {
            OriginalPreviewBrush.Color = Color.FromArgb(initial.A, initial.R, initial.G, initial.B);
            LoadPalette();
            UpdateAllUi();
        };
    }

    /// <summary>Apply the immersive dark-mode title bar attribute as soon as the HWND exists,
    /// so the system-drawn title bar matches the dark picker chrome instead of staying white.
    /// Win32 only — works on Windows 10 1809+ / Windows 11. Errors are silent (older Windows
    /// just keeps the default light title bar).</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int useDark = 1;
        // Try the modern attribute first (build 19041+); fall back to the pre-20H1 index
        // (build 18985–19040). Both write the same darken-titlebar flag.
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
        }
        // Win11 (build 22000+) honours per-window caption + border colour. Caption tinted
        // dark (the FluentWindow draws its own caption above this so the value mostly matters
        // before our content paints); border tinted to the runtime AccentForegroundDarkBrush
        // so the 1-px DWM outline matches the app's accent ring around other windows.
        int captionRef = 0x001E1E1E; // Win32 COLORREF: 0x00BBGGRR
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionRef, sizeof(int));
        var borderRef = ResolveAccentDarkColorRef();
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderRef, sizeof(int));
    }

    /// <summary>Read the live <c>AccentForegroundDarkBrush</c> from the app resource scope and
    /// pack it into a Win32 COLORREF (0x00BBGGRR). Falls back to a dark grey when the brush
    /// isn't a SolidColorBrush (the theme system can swap brushes at runtime so the lookup
    /// isn't strictly stable, but for the OnSourceInitialized one-shot it's fine).</summary>
    private static int ResolveAccentDarkColorRef()
    {
        try
        {
            if (System.Windows.Application.Current?.Resources["AccentForegroundDarkBrush"] is System.Windows.Media.SolidColorBrush b)
            {
                var c = b.Color;
                return (c.B << 16) | (c.G << 8) | c.R;
            }
        }
        catch { /* resource unavailable — fall through to default */ }
        return 0x001E1E1E;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_CAPTION_COLOR = 35;   // Win11 build 22000+
    private const int DWMWA_BORDER_COLOR = 34;    // Win11 build 22000+

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>The picked color when the dialog closes with OK. Read this only after ShowDialog() == true.</summary>
    public ShapeColor PickedColor { get; private set; } = ShapeColor.Black;

    public void ApplySampledColor(ShapeColor c)
    {
        var hsv = Hsv.FromRgb(c.R, c.G, c.B);
        _h = hsv.H;
        _s = hsv.S;
        _v = hsv.V;
        _a = c.A;
        UpdateAllUi();
    }

    public event EventHandler? EyedropperRequested;

    public event EventHandler<ShapeColor>? ColorChanged;

    // ── Wiring ─────────────────────────────────────────────────────────────────────

    private void WireSliderInputs()
    {
        HSlider.ValueChanged += (_, v) => { if (_suppress) return; _h = v / 360.0; UpdateAllUi(); };
        SSlider.ValueChanged += (_, v) => { if (_suppress) return; _s = v / 100.0; UpdateAllUi(); };
        VSlider.ValueChanged += (_, v) => { if (_suppress) return; _v = v / 100.0; UpdateAllUi(); };
        RSlider.ValueChanged += (_, v) => { if (_suppress) return; PushFromRgb((byte)Math.Round(v), null, null); };
        GSlider.ValueChanged += (_, v) => { if (_suppress) return; PushFromRgb(null, (byte)Math.Round(v), null); };
        BSlider.ValueChanged += (_, v) => { if (_suppress) return; PushFromRgb(null, null, (byte)Math.Round(v)); };
        ASlider.ValueChanged += (_, v) => { if (_suppress) return; _a = (byte)Math.Round(v); UpdateAllUi(); };

        CSlider.ValueChanged += (_, v) => { if (_suppress) return; PushFromCmyk(v, null, null, null); };
        MSlider.ValueChanged += (_, v) => { if (_suppress) return; PushFromCmyk(null, v, null, null); };
        YSlider.ValueChanged += (_, v) => { if (_suppress) return; PushFromCmyk(null, null, v, null); };
        KSlider.ValueChanged += (_, v) => { if (_suppress) return; PushFromCmyk(null, null, null, v); };

        HBox.LostFocus += (_, _) => { if (TryReadInt(HBox, 0, 360, out var v)) { _h = v / 360.0; UpdateAllUi(); } };
        SBox.LostFocus += (_, _) => { if (TryReadInt(SBox, 0, 100, out var v)) { _s = v / 100.0; UpdateAllUi(); } };
        VBox.LostFocus += (_, _) => { if (TryReadInt(VBox, 0, 100, out var v)) { _v = v / 100.0; UpdateAllUi(); } };
        RBox.LostFocus += (_, _) => { if (TryReadInt(RBox, 0, 255, out var v)) PushFromRgb((byte)v, null, null); };
        GBox.LostFocus += (_, _) => { if (TryReadInt(GBox, 0, 255, out var v)) PushFromRgb(null, (byte)v, null); };
        BBox.LostFocus += (_, _) => { if (TryReadInt(BBox, 0, 255, out var v)) PushFromRgb(null, null, (byte)v); };
        ABox.LostFocus += (_, _) => { if (TryReadInt(ABox, 0, 255, out var v)) { _a = (byte)v; UpdateAllUi(); } };
        CBox.LostFocus += (_, _) => { if (TryReadInt(CBox, 0, 100, out var v)) PushFromCmyk(v, null, null, null); };
        MBox.LostFocus += (_, _) => { if (TryReadInt(MBox, 0, 100, out var v)) PushFromCmyk(null, v, null, null); };
        YBox.LostFocus += (_, _) => { if (TryReadInt(YBox, 0, 100, out var v)) PushFromCmyk(null, null, v, null); };
        KBox.LostFocus += (_, _) => { if (TryReadInt(KBox, 0, 100, out var v)) PushFromCmyk(null, null, null, v); };

        // Hex + Decimal commit on every keystroke (with _suppress guard so the brush-update
        // round-trip from UpdateAllUi → set HexBox.Text doesn't re-enter). The user complaint
        // "set Hex doesn't update the preview" was the LostFocus-only handler not firing while
        // the user kept typing/clicking inside the box.
        HexBox.TextChanged += (_, _) => { if (!_suppress) OnHexCommitted(); };
        // Mark Enter handled INSIDE the TextBox: ClearFocus commits the value, e.Handled stops
        // the event from bubbling to the IsDefault OK button (which would close the dialog
        // mid-edit). A second Enter, with focus no longer on a TextBox, reaches OK normally.
        HexBox.KeyDown     += (_, ev) => { if (ev.Key == Key.Enter) { Keyboard.ClearFocus(); ev.Handled = true; } };
        DecBox.TextChanged += (_, _) => { if (!_suppress) OnDecimalCommitted(); };
        DecBox.KeyDown     += (_, ev) => { if (ev.Key == Key.Enter) { Keyboard.ClearFocus(); ev.Handled = true; } };
        // sRGB toggle: re-runs UpdateAllUi (re-paints sliders, swatches, New preview), rebuilds
        // the palette swatches, and re-rasterises the colour wheel — every visible "colour
        // value" surface follows the toggle for consistency. The wheel re-render is ~3 ms for
        // 360² pixels, which is fine on a click event.
        SrgbToggle.Click += (_, _) =>
        {
            UpdateAllUi();
            RebuildPalette();
            ColorWheel.PixelGamma = ApplyPreviewGamma;
            ColorSquare.PixelGamma = ApplyPreviewGamma;
            _persistedSrgbOff = SrgbToggle.IsChecked != true;
        };

        foreach (var b in new[] { HBox, SBox, VBox, RBox, GBox, BBox, ABox, CBox, MBox, YBox, KBox })
        {
            // Same handling as HEX / DEC — Enter commits without closing.
            b.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { Keyboard.ClearFocus(); ev.Handled = true; } };
        }
    }

    private static bool TryReadInt(TextBox box, int min, int max, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Clamp(value, min, max);
            return true;
        }
        value = 0;
        return false;
    }

    // ── State conversions ─────────────────────────────────────────────────────────

    private void PushFromRgb(byte? r, byte? g, byte? b)
    {
        var (cr, cg, cb) = new Hsv(_h, _s, _v).ToRgb();
        var nr = r ?? cr;
        var ng = g ?? cg;
        var nb = b ?? cb;
        var hsv = Hsv.FromRgb(nr, ng, nb);
        _h = hsv.H;
        _s = hsv.S;
        _v = hsv.V;
        UpdateAllUi();
    }

    private void PushFromCmyk(double? c, double? m, double? y, double? k)
    {
        var (cur_c, cur_m, cur_y, cur_k) = RgbToCmyk(new Hsv(_h, _s, _v).ToRgb());
        var nc = (c ?? cur_c) / 100.0;
        var nm = (m ?? cur_m) / 100.0;
        var ny = (y ?? cur_y) / 100.0;
        var nk = (k ?? cur_k) / 100.0;
        var (r, g, b) = CmykToRgb(nc, nm, ny, nk);
        var hsv = Hsv.FromRgb(r, g, b);
        _h = hsv.H;
        _s = hsv.S;
        _v = hsv.V;
        UpdateAllUi();
    }

    private void OnHexCommitted()
    {
        var s = HexBox.Text.Trim().TrimStart('#');
        try
        {
            byte a = _a, r, g, b;
            if (s.Length == 6)
            {
                // RRGGBB (fully opaque — alpha keeps its current value, doesn't get reset).
                r = Convert.ToByte(s[..2], 16); g = Convert.ToByte(s[2..4], 16); b = Convert.ToByte(s[4..6], 16);
            }
            else if (s.Length == 8)
            {
                // RRGGBBAA — CSS Color Module Level 4 ordering. Replaces the old AARRGGBB
                // parser, which was the WPF/Android convention but inconsistent with what
                // designers paste from Figma / browser DevTools / CSS files.
                r = Convert.ToByte(s[..2], 16);
                g = Convert.ToByte(s[2..4], 16);
                b = Convert.ToByte(s[4..6], 16);
                a = Convert.ToByte(s[6..8], 16);
            }
            else { return; }
            _a = a;
            var hsv = Hsv.FromRgb(r, g, b);
            _h = hsv.H; _s = hsv.S; _v = hsv.V;
            UpdateAllUi();
        }
        catch (FormatException) { }
        catch (OverflowException) { }
    }

    /// <summary>Parses a packed-AARRGGBB integer (the same form <see cref="FormatDecimal"/>
    /// emits) and pushes it through HSV. Silent on parse failure so partial typing — e.g. the
    /// user halfway through entering a 10-digit number — doesn't mangle state.</summary>
    private void OnDecimalCommitted()
    {
        if (!uint.TryParse(DecBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var packed))
            return;
        var a = (byte)((packed >> 24) & 0xFF);
        var r = (byte)((packed >> 16) & 0xFF);
        var g = (byte)((packed >> 8) & 0xFF);
        var b = (byte)(packed & 0xFF);
        _a = a;
        var hsv = Hsv.FromRgb(r, g, b);
        _h = hsv.H; _s = hsv.S; _v = hsv.V;
        UpdateAllUi();
    }

    /// <summary>Standard CMYK conversion. Returns C/M/Y/K as percentages 0-100.</summary>
    private static (double C, double M, double Y, double K) RgbToCmyk((byte R, byte G, byte B) rgb)
    {
        double r = rgb.R / 255.0, g = rgb.G / 255.0, b = rgb.B / 255.0;
        var k = 1.0 - Math.Max(r, Math.Max(g, b));
        if (k >= 0.99999) return (0, 0, 0, 100);   // pure black — undefined CMY
        var c = (1 - r - k) / (1 - k);
        var m = (1 - g - k) / (1 - k);
        var y = (1 - b - k) / (1 - k);
        return (c * 100.0, m * 100.0, y * 100.0, k * 100.0);
    }

    /// <summary>Standard CMYK→RGB conversion. Inputs are 0-1.</summary>
    private static (byte R, byte G, byte B) CmykToRgb(double c, double m, double y, double k)
    {
        var r = (byte)Math.Round(255 * (1 - c) * (1 - k));
        var g = (byte)Math.Round(255 * (1 - m) * (1 - k));
        var b = (byte)Math.Round(255 * (1 - y) * (1 - k));
        return (r, g, b);
    }

    // ── UI sync ─────────────────────────────────────────────────────────────────────

    private void UpdateAllUi()
    {
        _suppress = true;
        try
        {
            var (r, g, b) = new Hsv(_h, _s, _v).ToRgb();
            var (c, m, y, k) = RgbToCmyk((r, g, b));

            HSlider.Value = _h * 360.0;
            SSlider.Value = _s * 100.0;
            VSlider.Value = _v * 100.0;
            RSlider.Value = r;
            GSlider.Value = g;
            BSlider.Value = b;
            ASlider.Value = _a;
            CSlider.Value = c;
            MSlider.Value = m;
            YSlider.Value = y;
            KSlider.Value = k;

            HBox.Text = ((int)Math.Round(_h * 360)).ToString(CultureInfo.InvariantCulture);
            SBox.Text = ((int)Math.Round(_s * 100)).ToString(CultureInfo.InvariantCulture);
            VBox.Text = ((int)Math.Round(_v * 100)).ToString(CultureInfo.InvariantCulture);
            RBox.Text = r.ToString(CultureInfo.InvariantCulture);
            GBox.Text = g.ToString(CultureInfo.InvariantCulture);
            BBox.Text = b.ToString(CultureInfo.InvariantCulture);
            ABox.Text = _a.ToString(CultureInfo.InvariantCulture);
            CBox.Text = ((int)Math.Round(c)).ToString(CultureInfo.InvariantCulture);
            MBox.Text = ((int)Math.Round(m)).ToString(CultureInfo.InvariantCulture);
            YBox.Text = ((int)Math.Round(y)).ToString(CultureInfo.InvariantCulture);
            KBox.Text = ((int)Math.Round(k)).ToString(CultureInfo.InvariantCulture);
            // Hex display follows CSS Color Module Level 4: RRGGBB when fully opaque, RRGGBBAA
            // (alpha appended) when there's transparency. Matches what users paste into design
            // tools / web stylesheets and aligns with CopyColorAsHexTask's output.
            HexBox.Text = _a == 255
                ? $"#{r:X2}{g:X2}{b:X2}"
                : $"#{r:X2}{g:X2}{b:X2}{_a:X2}";
            DecBox.Text = (((uint)_a << 24) | ((uint)r << 16) | ((uint)g << 8) | b)
                .ToString(CultureInfo.InvariantCulture);

            // Push HSV into both 2D pickers so their cursor + brightness state stay in lockstep
            // with the rest of the picker. _suppress is already set, so PointPicked handlers
            // (which would otherwise loop right back into UpdateAllUi) bail at their guard. The
            // hidden control still receives updates — switching shape doesn't lose state.
            ColorWheel.H = _h;
            ColorWheel.S = _s;
            ColorWheel.V = _v;
            ColorSquare.H = _h;
            ColorSquare.S = _s;
            ColorSquare.V = _v;

            // Preview swatches respect the sRGB toggle: ON = display the byte values straight (the
            // OS / display already applies sRGB gamma during compositing). OFF = treat the bytes
            // as if they represented LINEAR values, applying linear→sRGB so the user sees the
            // perceptually-different rendering you'd get if you handed those bytes to a renderer
            // that interprets them linearly (e.g. an Unreal material). This is exactly the
            // behaviour the user expects from the Unreal-style "sRGB Preview" checkbox.
            // sRGB toggle only affects the NEW pick — the Original is the colour the dialog was
            // opened with and serves as a stable reference (otherwise toggling sRGB would shift
            // both swatches together, defeating the comparison).
            var (pr, pg, pb) = ApplyPreviewGamma(r, g, b);
            NewPreviewBrush.Color = Color.FromArgb(_a, pr, pg, pb);
            OriginalPreviewBrush.Color = Color.FromArgb(_originalColor.A, _originalColor.R, _originalColor.G, _originalColor.B);

            // OK button: by default bound via XAML DynamicResource to the global accent brushes,
            // so it always shows the real applied accent. The Theme tab overrides Background /
            // Foreground (via the public OkButton field) to give the user a live preview of the
            // pick they're making — see MainWindow.PickAccentColor for the wiring.

            RefreshGradients(r, g, b, c, m, y, k);
        }
        finally { _suppress = false; }

        var current = new ShapeColor(_a, ((Hsv)new Hsv(_h, _s, _v)).ToRgb().R,
                                          ((Hsv)new Hsv(_h, _s, _v)).ToRgb().G,
                                          ((Hsv)new Hsv(_h, _s, _v)).ToRgb().B);
        ColorChanged?.Invoke(this, current);
    }

    /// <summary>Recompute every slider's gradient track based on the current state of the OTHER
    /// channels — this is what makes the sliders Unreal-style "see what dragging will produce".
    /// Cheap to do every UpdateAllUi: just builds 11 LinearGradientBrushes. Endpoints go through
    /// <see cref="ApplyPreviewGamma"/> so the sliders match Unreal's behaviour: when sRGB Preview
    /// is OFF, every gradient/swatch reflects the linear→sRGB mapping (the wheel stays put — see
    /// the explanation in the user-facing docs).</summary>
    private void RefreshGradients(byte r, byte g, byte b, double c, double m, double y, double k)
    {
        // Hue is rainbow regardless of other channels — but its endpoints still get the gamma
        // pass so sRGB-OFF darkens the rainbow consistently with everything else.
        HSlider.TrackBrush = HueRainbow(ApplyPreviewGamma);

        // Saturation: from grayscale-at-current-V to full-color-at-current-H+V.
        var (greyR, greyG, greyB) = new Hsv(_h, 0, _v).ToRgb();
        var (fullR, fullG, fullB) = new Hsv(_h, 1, _v).ToRgb();
        SSlider.TrackBrush = HorizontalGradient(GammaColor(greyR, greyG, greyB), GammaColor(fullR, fullG, fullB));

        var (vTopR, vTopG, vTopB) = new Hsv(_h, _s, 1).ToRgb();
        VSlider.TrackBrush = HorizontalGradient(GammaColor(0, 0, 0), GammaColor(vTopR, vTopG, vTopB));

        RSlider.TrackBrush = HorizontalGradient(GammaColor(0, g, b),   GammaColor(255, g, b));
        GSlider.TrackBrush = HorizontalGradient(GammaColor(r, 0, b),   GammaColor(r, 255, b));
        BSlider.TrackBrush = HorizontalGradient(GammaColor(r, g, 0),   GammaColor(r, g, 255));

        // Alpha: transparent → fully-opaque current colour. The slider shows a checker behind so
        // you can see the result through the gradient. Alpha endpoint colour goes through gamma
        // so it matches the New-preview swatch when sRGB toggle changes.
        var (ar, ag, ab) = ApplyPreviewGamma(r, g, b);
        ASlider.TrackBrush = HorizontalGradient(
            Color.FromArgb(0,   ar, ag, ab),
            Color.FromArgb(255, ar, ag, ab));

        // CMYK: sliding each channel 0→100% with others held.
        var c01 = c / 100.0; var m01 = m / 100.0; var y01 = y / 100.0; var k01 = k / 100.0;
        var (cMin_R, cMin_G, cMin_B) = CmykToRgb(0, m01, y01, k01);
        var (cMax_R, cMax_G, cMax_B) = CmykToRgb(1, m01, y01, k01);
        CSlider.TrackBrush = HorizontalGradient(GammaColor(cMin_R, cMin_G, cMin_B), GammaColor(cMax_R, cMax_G, cMax_B));

        var (mMin_R, mMin_G, mMin_B) = CmykToRgb(c01, 0, y01, k01);
        var (mMax_R, mMax_G, mMax_B) = CmykToRgb(c01, 1, y01, k01);
        MSlider.TrackBrush = HorizontalGradient(GammaColor(mMin_R, mMin_G, mMin_B), GammaColor(mMax_R, mMax_G, mMax_B));

        var (yMin_R, yMin_G, yMin_B) = CmykToRgb(c01, m01, 0, k01);
        var (yMax_R, yMax_G, yMax_B) = CmykToRgb(c01, m01, 1, k01);
        YSlider.TrackBrush = HorizontalGradient(GammaColor(yMin_R, yMin_G, yMin_B), GammaColor(yMax_R, yMax_G, yMax_B));

        var (kMin_R, kMin_G, kMin_B) = CmykToRgb(c01, m01, y01, 0);
        KSlider.TrackBrush = HorizontalGradient(GammaColor(kMin_R, kMin_G, kMin_B), GammaColor(0, 0, 0));
    }

    /// <summary>RGB byte triple → <see cref="Color"/>, with sRGB-toggle gamma applied. Convenience
    /// wrapper around <see cref="ApplyPreviewGamma"/> used everywhere we render colours that
    /// represent picked / pickable values (sliders, palette swatches).</summary>
    private Color GammaColor(byte r, byte g, byte b)
    {
        var (pr, pg, pb) = ApplyPreviewGamma(r, g, b);
        return Color.FromRgb(pr, pg, pb);
    }

    private static Brush HorizontalGradient(Color start, Color end)
    {
        var b = new LinearGradientBrush(start, end, new Point(0, 0.5), new Point(1, 0.5));
        b.Freeze();
        return b;
    }

    /// <summary>Hue-rainbow gradient. <paramref name="gamma"/> is applied to each stop so the
    /// rainbow consistently darkens / brightens with the picker's sRGB toggle (Unreal-style).</summary>
    private static Brush HueRainbow(Func<byte, byte, byte, (byte R, byte G, byte B)> gamma)
    {
        Color G(byte r, byte g, byte bb)
        {
            var (pr, pg, pbb) = gamma(r, g, bb);
            return Color.FromRgb(pr, pg, pbb);
        }
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(G(0xFF, 0x00, 0x00), 0.000),
                new GradientStop(G(0xFF, 0xFF, 0x00), 0.167),
                new GradientStop(G(0x00, 0xFF, 0x00), 0.333),
                new GradientStop(G(0x00, 0xFF, 0xFF), 0.500),
                new GradientStop(G(0x00, 0x00, 0xFF), 0.667),
                new GradientStop(G(0xFF, 0x00, 0xFF), 0.833),
                new GradientStop(G(0xFF, 0x00, 0x00), 1.000),
            }
        };
        b.Freeze();
        return b;
    }

    // ── SV square ─────────────────────────────────────────────────────────────────

    // ── sRGB / linear preview gamma ─────────────────────────────────────────────────

    /// <summary>When the sRGB toggle is ON we leave the byte values alone (the display already
    /// applies sRGB gamma — what the user sees IS sRGB). When OFF we treat the same bytes as
    /// LINEAR values and apply linear→sRGB so the swatch shows the perceptually-different result
    /// you'd get if a renderer interpreted those bytes linearly (Unreal-style).</summary>
    private (byte R, byte G, byte B) ApplyPreviewGamma(byte r, byte g, byte b)
    {
        if (SrgbToggle is null || SrgbToggle.IsChecked == true) return (r, g, b);
        return (LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b));
    }

    /// <summary>Standard sRGB EOTF (linear → sRGB), expecting 0–255 byte input. Piecewise to
    /// match the official IEC 61966-2-1 transfer function — gamma 2.2 alone would visibly
    /// disagree near the toe.</summary>
    private static byte LinearToSrgb(byte channel)
    {
        var c = channel / 255.0;
        var s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
    }


    // ── RGB / CMYK tab ─────────────────────────────────────────────────────────────

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        // Wired in XAML before fields are initialized, so guard against null UI.
        if (RgbBlock is null || CmykBlock is null) return;
        RgbBlock.Visibility  = RgbTab.IsChecked == true  ? Visibility.Visible : Visibility.Collapsed;
        CmykBlock.Visibility = CmykTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _persistedCmykTab = CmykTab.IsChecked == true;
    }

    /// <summary>Toggle Wheel / Square 2D picker. Both controls live in the same Grid cell; this
    /// just flips Visibility. State (H/S/V) is shared via dependency-property bindings + the
    /// UpdateAllUi pass, so switching is instantaneous and never drops the current pick.</summary>
    private void OnShapeChanged(object sender, RoutedEventArgs e)
    {
        // Skip the init-time firing: the RadioButton's Checked event also fires when XAML
        // applies IsChecked="True" during construction, but at that point the visibility is
        // already what the handler would set anyway. Gating on IsLoaded keeps the breakpoint
        // quiet when the picker opens — handler only runs on real user toggles. The session
        // restore path applies visibility directly, so it doesn't depend on this firing.
        if (!IsLoaded) return;
        if (ColorWheel is null || ColorSquare is null) return;
        ColorWheel.Visibility  = WheelShape.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
        ColorSquare.Visibility = SquareShape.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _persistedSquareShape = SquareShape.IsChecked == true;
    }

    /// <summary>Apply the static-cached UI preferences (Wheel/Square, RGB/CMYK, sRGB) so reopening
    /// the picker keeps the user's last-chosen modes. Called once during construction, after
    /// WireSliderInputs has hooked the handlers we'll write back through.</summary>
    private void RestorePersistedPreferences()
    {
        if (_persistedSquareShape)
        {
            SquareShape.IsChecked = true;
            // OnShapeChanged is gated on IsLoaded so it bails during ctor — apply the visibility
            // flip directly here. Without this the dialog would show the radio as Square but the
            // wheel still rendered behind it.
            ColorWheel.Visibility  = Visibility.Collapsed;
            ColorSquare.Visibility = Visibility.Visible;
        }
        if (_persistedCmykTab)
        {
            // OnTabChanged is null-guarded only and fields are non-null after InitializeComponent,
            // so it runs naturally and flips the slider blocks.
            CmykTab.IsChecked = true;
        }
        if (_persistedSrgbOff)
        {
            // CheckBox.Click doesn't fire on programmatic IsChecked changes, so the slider
            // gradients / palette / wheel re-rasterisation that the click handler triggers
            // happen naturally via the Loaded handler's UpdateAllUi + RebuildPalette + the
            // ColorWheel/Square.PixelGamma assignments later in this constructor.
            SrgbToggle.IsChecked = false;
        }
    }

    // ── Palette ─────────────────────────────────────────────────────────────────────

    private readonly ObservableCollection<PaletteEntry> _paletteItems = [];

    private void LoadPalette()
    {
        PaletteHost.ItemsSource = _paletteItems;
        RebuildPalette();
    }

    private void RebuildPalette()
    {
        if (_paletteItems is null) return;
        _paletteItems.Clear();
        // Recent picks only — Standard palette removed since users rarely reached for it and
        // the radio toggle was just chrome. The picks live in ColorSwatchButton.CurrentRecents,
        // populated by the host (Theme tab / wheel launcher / editor) before ShowDialog.
        var source = ColorSwatchButton.CurrentRecents;
        // Pad / truncate to 10 swatches — the grid is 10×1 and lives compact under the copy
        // buttons. Going wider would either bleed past the wheel column or push the picker tall
        // again. Each swatch's brush goes through gamma so toggling sRGB Preview shifts the
        // palette in sync with the New-preview swatch + slider gradients.
        for (var i = 0; i < 10; i++)
        {
            var color = i < source.Count ? source[i] : ShapeColor.Transparent;
            Brush brush;
            if (color.IsTransparent)
            {
                brush = (Brush)Application.Current.Resources["CheckerBrush"];
            }
            else
            {
                var (pr, pg, pb) = ApplyPreviewGamma(color.R, color.G, color.B);
                brush = new SolidColorBrush(Color.FromArgb(color.A, pr, pg, pb));
            }
            _paletteItems.Add(new PaletteEntry(color, brush));
        }
    }

    private void OnPaletteSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not ShapeColor c || c.IsTransparent) return;
        ApplySampledColor(c);
        if (e.ClickCount >= 2)
        {
            CommitOk();
        }
    }

    private sealed record PaletteEntry(ShapeColor Color, Brush Brush);

    // ── Copy formatters ─────────────────────────────────────────────────────────────

    private (byte R, byte G, byte B) CurrentRgb() => new Hsv(_h, _s, _v).ToRgb();

    private string FormatRgb()
    {
        var (r, g, b) = CurrentRgb();
        return _a == 255
            ? $"rgb({r}, {g}, {b})"
            : $"rgba({r}, {g}, {b}, {(_a / 255.0).ToString("0.##", CultureInfo.InvariantCulture)})";
    }

    private string FormatHex()
    {
        // CSS Color Module Level 4 — same convention as the HexBox display: RRGGBB when fully
        // opaque, RRGGBBAA when there's transparency. The picker's inline 📋 button uses this;
        // pipeline tasks have their own format with optional toggles.
        var (r, g, b) = CurrentRgb();
        return _a == 255
            ? $"#{r:X2}{g:X2}{b:X2}"
            : $"#{r:X2}{g:X2}{b:X2}{_a:X2}";
    }

    private string FormatCmyk()
    {
        var (c, m, y, k) = RgbToCmyk(CurrentRgb());
        return $"cmyk({c.ToString("0.#", CultureInfo.InvariantCulture)}%, " +
               $"{m.ToString("0.#", CultureInfo.InvariantCulture)}%, " +
               $"{y.ToString("0.#", CultureInfo.InvariantCulture)}%, " +
               $"{k.ToString("0.#", CultureInfo.InvariantCulture)}%)";
    }

    private string FormatHsb() =>
        $"hsb({(int)Math.Round(_h * 360)}°, {(int)Math.Round(_s * 100)}%, {(int)Math.Round(_v * 100)}%)";

    private string FormatDecimal()
    {
        var (r, g, b) = CurrentRgb();
        var packed = ((uint)_a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        return packed.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Unreal Engine FLinearColor stringification — normalised 0–1 floats, 6 decimals.
    /// Format: <c>(R=1.000000,G=0.000000,B=0.000000,A=1.000000)</c>. Pasteable directly into
    /// UE asset properties / Blueprint defaults.</summary>
    private string FormatLinearRgb()
    {
        var (r, g, b) = CurrentRgb();
        string F(byte c) => (c / 255.0).ToString("0.000000", CultureInfo.InvariantCulture);
        return $"(R={F(r)},G={F(g)},B={F(b)},A={F(_a)})";
    }

    /// <summary>Unreal Engine FColor stringification — integer bytes in <b>B,G,R,A</b> order.
    /// Format: <c>(B=109,G=134,R=255,A=255)</c>. Note the unusual channel order — that's how
    /// FColor lays out its members and how its ToString emits them.</summary>
    private string FormatBgra()
    {
        var (r, g, b) = CurrentRgb();
        return $"(B={b},G={g},R={r},A={_a})";
    }

    private void OnCopyAllClicked(object sender, RoutedEventArgs e)
    {
        // Dump every format on its own line — pasting this into an issue / commit message
        // gives whoever reads it the colour in whatever notation their tooling expects.
        var sb = new StringBuilder()
            .AppendLine(FormatRgb())
            .AppendLine(FormatHex())
            .AppendLine(FormatCmyk())
            .AppendLine(FormatHsb())
            .AppendLine(FormatDecimal())
            .AppendLine(FormatLinearRgb())
            .Append(FormatBgra());
        SafeCopy(sb.ToString());
    }
    private void OnCopyRgbClicked(object sender, RoutedEventArgs e)     => SafeCopy(FormatRgb());
    private void OnCopyHexClicked(object sender, RoutedEventArgs e)     => SafeCopy(FormatHex());
    private void OnCopyCmykClicked(object sender, RoutedEventArgs e)    => SafeCopy(FormatCmyk());
    private void OnCopyHsbClicked(object sender, RoutedEventArgs e)     => SafeCopy(FormatHsb());
    private void OnCopyDecimalClicked(object sender, RoutedEventArgs e) => SafeCopy(FormatDecimal());
    private void OnCopyLinearClicked(object sender, RoutedEventArgs e)  => SafeCopy(FormatLinearRgb());
    private void OnCopyBgraClicked(object sender, RoutedEventArgs e)    => SafeCopy(FormatBgra());

    private static void SafeCopy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard may be locked momentarily — silent retry isn't worth it */ }
    }

    // ── Final actions ───────────────────────────────────────────────────────────────

    private void OnEyedropperClicked(object sender, RoutedEventArgs e) => EyedropperRequested?.Invoke(this, EventArgs.Empty);

    private void OnOkClicked(object sender, RoutedEventArgs e) => CommitOk();

    private void CommitOk()
    {
        var (r, g, b) = CurrentRgb();
        PickedColor = new ShapeColor(_a, r, g, b);
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Apply UI translations for the user-facing labels that aren't pure technical
    /// notation. AresToys.Editor doesn't reference AresToys.App so {Markup:Loc …} bindings can't
    /// resolve here — instead the host (ColorWheelLauncher in AresToys.App) calls this method
    /// after construction with the strings already resolved through Strings.ResourceManager.
    /// Keys not in the dictionary are left untouched, so callers can localise gradually and
    /// English fallbacks remain visible for any new label.</summary>
    public void ApplyLocalization(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.TryGetValue("Title", out var title)) TitleBarHeader.Title = title;
        if (labels.TryGetValue("Recent", out var recent)) RecentLabel.Text = recent;
        if (labels.TryGetValue("Sampler", out var sampler)) SamplerBtn.Content = sampler;
        if (labels.TryGetValue("Wheel", out var wheel)) WheelShape.Content = wheel;
        if (labels.TryGetValue("Square", out var square)) SquareShape.Content = square;
        if (labels.TryGetValue("CopyAll", out var copyAll)) CopyAllBtn.Content = copyAll;
        if (labels.TryGetValue("CopyRgb", out var copyRgb)) CopyRgbBtn.Content = copyRgb;
        if (labels.TryGetValue("CopyCmyk", out var copyCmyk)) CopyCmykBtn.Content = copyCmyk;
        if (labels.TryGetValue("CopyHsb", out var copyHsb)) CopyHsbBtn.Content = copyHsb;
        if (labels.TryGetValue("CopyLinear", out var copyLinear)) CopyLinearBtn.Content = copyLinear;
        if (labels.TryGetValue("CopyBgra", out var copyBgra)) CopyBgraBtn.Content = copyBgra;
        if (labels.TryGetValue("CopyHex", out var copyHex)) CopyHexBtn.Content = copyHex;
        if (labels.TryGetValue("CopyDec", out var copyDec)) CopyDecBtn.Content = copyDec;
        if (labels.TryGetValue("Ok", out var ok)) OkButton.Content = ok;
        if (labels.TryGetValue("Cancel", out var cancel)) CancelButton.Content = cancel;

        // Tooltip translations — keys with "Tooltip" prefix flow into ToolTip on the matching
        // control. Notation-only tooltips (rgb(R,G,B) etc.) are resx-managed too so the
        // translator can shorten them or swap the locale's decimal separator if needed.
        if (labels.TryGetValue("TooltipCopyAll", out var t1)) CopyAllBtn.ToolTip = t1;
        if (labels.TryGetValue("TooltipCopyRgb", out var t2)) CopyRgbBtn.ToolTip = t2;
        if (labels.TryGetValue("TooltipCopyCmyk", out var t3)) CopyCmykBtn.ToolTip = t3;
        if (labels.TryGetValue("TooltipCopyHsb", out var t4)) CopyHsbBtn.ToolTip = t4;
        if (labels.TryGetValue("TooltipCopyLinear", out var t5)) CopyLinearBtn.ToolTip = t5;
        if (labels.TryGetValue("TooltipCopyBgra", out var t6)) CopyBgraBtn.ToolTip = t6;
        if (labels.TryGetValue("TooltipCopyHex", out var t7)) CopyHexBtn.ToolTip = t7;
        if (labels.TryGetValue("TooltipCopyDec", out var t8)) CopyDecBtn.ToolTip = t8;
        if (labels.TryGetValue("TooltipSampler", out var t9)) SamplerBtn.ToolTip = t9;
        if (labels.TryGetValue("TooltipSrgb", out var t10)) SrgbToggle.ToolTip = t10;
        if (labels.TryGetValue("TooltipWheel", out var t11)) WheelShape.ToolTip = t11;
        if (labels.TryGetValue("TooltipSquare", out var t12)) SquareShape.ToolTip = t12;
    }
}
