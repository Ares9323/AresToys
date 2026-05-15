using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AresToys.Storage.Settings;
using Wpf.Ui.Appearance;

namespace AresToys.App.Services;

/// <summary>Holds the two user-tunable accent colors (background + foreground) and applies them
/// globally so every accent-aware control — Primary buttons, ToggleSwitches, our drop indicators,
/// the pinned-window border — picks them up without local plumbing. Persists hex strings via
/// <see cref="ISettingsStore"/> under <c>theme.accent_bg</c> / <c>theme.accent_fg</c>.</summary>
/// <remarks>The accent flows out through two channels:
/// <list type="bullet">
/// <item><description>WPF-UI's <see cref="ApplicationAccentColorManager"/>: this paints
/// SystemAccentColor* resources, which all WPF-UI Primary controls bind to. The fg color also
/// overrides <c>TextOnAccentFillColorPrimaryBrush</c> so button labels read correctly on top of
/// custom backgrounds.</description></item>
/// <item><description>App-level resources <c>AccentBackgroundBrush</c> / <c>AccentForegroundBrush</c>:
/// used directly in XAML wherever we want the same color but the surface isn't a Primary
/// WPF-UI control (e.g. the pinned-image border, the drop-target line in workflows).</description></item>
/// </list></remarks>
public sealed class ThemeService
{
    private const string BgKey = "theme.accent_bg";
    private const string FgKey = "theme.accent_fg";
    private const string DarkKey = "theme.accent_dark";
    private const string FgDarkKey = "theme.accent_fg_dark";
    private const string DeleteKey = "theme.accent_delete";
    private const string Surface1Key = "theme.surface_1";
    private const string Surface2Key = "theme.surface_2";
    private const string Surface3Key = "theme.surface_3";
    public static readonly Color DefaultBackground = (Color)ColorConverter.ConvertFromString("#6BA780")!;
    public static readonly Color DefaultForeground = Colors.White;
    /// <summary>Default for the dim subtext colour — captions, age labels, kind/source rows in
    /// the clipboard list, swatch descriptions in the theme tab. One global control over how
    /// muted the secondary text reads against the surface tones.</summary>
    public static readonly Color DefaultAccentForegroundDark = (Color)ColorConverter.ConvertFromString("#878787")!;
    /// <summary>Default for the "dark accent" — used as the canvas / inactive surface in the
    /// launcher overlay (cell background, inactive tab headers). Sits low on the value axis so
    /// the brighter accent reads on top of it without contrast issues. Picked to pair with the
    /// default green accent (matches the SVG logo's brand colour) so the launcher's
    /// "active vs ambient" tabs read as the same hue family out of the box.</summary>
    public static readonly Color DefaultAccentDark = (Color)ColorConverter.ConvertFromString("#314D3B")!;
    /// <summary>Default accent for destructive actions — Delete buttons, the trash icon in the
    /// clipboard toolbar, and any other "this is irreversible" affordance. Picked to clash
    /// readably with both the green and the blue presets without being a screaming pure red.
    /// User-tunable from Settings → Theme so they can dial it up/down for their preset.</summary>
    public static readonly Color DefaultAccentDelete = (Color)ColorConverter.ConvertFromString("#8F2720")!;
    /// <summary>Three neutral surface colours, ordered darkest → lightest. Surface1 is for the
    /// deepest backgrounds (input backgrounds, list rows), Surface2 for the standard window
    /// chrome (popup body, launcher card), Surface3 for elevated panels (drag handles, group
    /// containers). Centralising them lets the user retune the entire greyscale palette from
    /// one place instead of editing dozens of hex literals across XAML.</summary>
    public static readonly Color DefaultSurface1 = (Color)ColorConverter.ConvertFromString("#1A1A1A")!;
    public static readonly Color DefaultSurface2 = (Color)ColorConverter.ConvertFromString("#1F1F1F")!;
    public static readonly Color DefaultSurface3 = (Color)ColorConverter.ConvertFromString("#2D2D2D")!;

    private readonly ISettingsStore _settings;

    private Color _bg = DefaultBackground;
    private Color _fg = DefaultForeground;
    private Color _dark = DefaultAccentDark;
    private Color _fgDark = DefaultAccentForegroundDark;
    private Color _delete = DefaultAccentDelete;
    private Color _surface1 = DefaultSurface1;
    private Color _surface2 = DefaultSurface2;
    private Color _surface3 = DefaultSurface3;

    public ThemeService(ISettingsStore settings) => _settings = settings;

    public Color AccentBackground => _bg;
    public Color AccentForeground => _fg;
    public Color AccentBackgroundDark => _dark;
    public Color AccentForegroundDark => _fgDark;
    public Color AccentDelete => _delete;
    public Color Surface1 => _surface1;
    public Color Surface2 => _surface2;
    public Color Surface3 => _surface3;

    /// <summary>Fires after either color changes (via <see cref="SetAsync"/> or
    /// <see cref="LoadAsync"/>). The Theme view-model uses this to re-sync its hex inputs when the
    /// user clicks Reset, since Reset writes via the service rather than setting the hex strings
    /// directly.</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var bgRaw     = await _settings.GetAsync(BgKey, ct).ConfigureAwait(false);
        var fgRaw     = await _settings.GetAsync(FgKey, ct).ConfigureAwait(false);
        var darkRaw   = await _settings.GetAsync(DarkKey, ct).ConfigureAwait(false);
        var fgDarkRaw = await _settings.GetAsync(FgDarkKey, ct).ConfigureAwait(false);
        var deleteRaw = await _settings.GetAsync(DeleteKey, ct).ConfigureAwait(false);
        var s1Raw     = await _settings.GetAsync(Surface1Key, ct).ConfigureAwait(false);
        var s2Raw     = await _settings.GetAsync(Surface2Key, ct).ConfigureAwait(false);
        var s3Raw     = await _settings.GetAsync(Surface3Key, ct).ConfigureAwait(false);
        _bg       = TryParseHex(bgRaw)     ?? DefaultBackground;
        _fg       = TryParseHex(fgRaw)     ?? DefaultForeground;
        _dark     = TryParseHex(darkRaw)   ?? DefaultAccentDark;
        _fgDark   = TryParseHex(fgDarkRaw) ?? DefaultAccentForegroundDark;
        _delete   = TryParseHex(deleteRaw) ?? DefaultAccentDelete;
        _surface1 = TryParseHex(s1Raw)     ?? DefaultSurface1;
        _surface2 = TryParseHex(s2Raw)     ?? DefaultSurface2;
        _surface3 = TryParseHex(s3Raw)     ?? DefaultSurface3;
        Apply();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetAsync(Color background, Color foreground, Color dark, Color foregroundDark,
        Color delete, Color surface1, Color surface2, Color surface3, CancellationToken ct = default)
    {
        _bg = background;
        _fg = foreground;
        _dark = dark;
        _fgDark = foregroundDark;
        _delete = delete;
        _surface1 = surface1;
        _surface2 = surface2;
        _surface3 = surface3;
        Apply();
        await _settings.SetAsync(BgKey,       ToHex(background),     sensitive: false, ct).ConfigureAwait(false);
        await _settings.SetAsync(FgKey,       ToHex(foreground),     sensitive: false, ct).ConfigureAwait(false);
        await _settings.SetAsync(DarkKey,     ToHex(dark),           sensitive: false, ct).ConfigureAwait(false);
        await _settings.SetAsync(FgDarkKey,   ToHex(foregroundDark), sensitive: false, ct).ConfigureAwait(false);
        await _settings.SetAsync(DeleteKey,   ToHex(delete),         sensitive: false, ct).ConfigureAwait(false);
        await _settings.SetAsync(Surface1Key, ToHex(surface1),       sensitive: false, ct).ConfigureAwait(false);
        await _settings.SetAsync(Surface2Key, ToHex(surface2),       sensitive: false, ct).ConfigureAwait(false);
        await _settings.SetAsync(Surface3Key, ToHex(surface3),       sensitive: false, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task ResetAsync(CancellationToken ct = default) =>
        SetAsync(DefaultBackground, DefaultForeground, DefaultAccentDark, DefaultAccentForegroundDark,
            DefaultAccentDelete, DefaultSurface1, DefaultSurface2, DefaultSurface3, ct);

    /// <summary>Pushes the current colors into Application.Resources and the WPF-UI accent
    /// manager. Called on Load and on every Set; safe to call from any thread (marshals to the UI
    /// dispatcher because Application.Resources is a UI-thread object).</summary>
    /// <remarks>Why we override individual Fluent brushes instead of trusting
    /// <see cref="ApplicationAccentColorManager.Apply"/> alone: WPF-UI v4 declares its accent
    /// brushes (e.g. <c>AccentFillColorDefaultBrush</c>) via <c>StaticResource</c> bindings to the
    /// <c>SystemAccentColor*</c> color keys in its merged dictionary. Apply() updates the colors,
    /// but the brushes' <c>Color</c> property was already snapshotted at template-instantiation
    /// time — so just changing colors doesn't repaint live. Replacing the brush objects themselves
    /// at <c>Application.Resources</c> overrides the merged dictionary lookup and triggers the
    /// DynamicResource refresh on every consumer (Primary buttons, ToggleSwitch, etc.).</remarks>
    private void Apply()
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(Apply);
            return;
        }

        var bgBrush = new SolidColorBrush(_bg); bgBrush.Freeze();
        var fgBrush = new SolidColorBrush(_fg); fgBrush.Freeze();
        // Hoisted up so the SystemColors.* overrides below (which need the dim foreground and
        // the dark accent) can resolve them. The matching app.Resources["AccentForeground*"]
        // assignments still happen further down where the rest of the secondary-text family
        // lives — moving them too would scatter the per-key writes across two blocks.
        var fgDarkBrush = new SolidColorBrush(_fgDark); fgDarkBrush.Freeze();
        var darkBrush = new SolidColorBrush(_dark); darkBrush.Freeze();

        // Lighter / darker shades for hover/pressed states. WPF-UI v4 uses three accent variants
        // (Primary > Secondary > Tertiary) where Secondary is slightly lighter and Tertiary
        // slightly darker than the base — same convention as the system accent. Cheap perceptual
        // approximation that works on any user-chosen accent without recomputing luminance.
        var lighterBrush = new SolidColorBrush(Lighten(_bg, 0.15)); lighterBrush.Freeze();
        var darkerBrush  = new SolidColorBrush(Lighten(_bg, -0.15)); darkerBrush.Freeze();

        // Our own keys — used directly in XAML (DynamicResource AccentBackgroundBrush). The pin
        // window border, drop indicator and Theme sample preview consume these.
        app.Resources["AccentBackgroundColor"] = _bg;
        app.Resources["AccentForegroundColor"] = _fg;
        app.Resources["AccentBackgroundBrush"] = bgBrush;
        app.Resources["AccentForegroundBrush"] = fgBrush;

        // Lighter accent variants for hyperlinks / muted highlights / anywhere the primary
        // accent reads too saturated against dark surfaces. Re-uses the same Lighten helper as
        // the WPF-UI Secondary/Tertiary derivation, so all "lighter accent" elements stay in
        // family no matter which accent the user picks.
        var accentLighterColor = Lighten(_bg, 0.30);
        var accentLighterBrush = new SolidColorBrush(accentLighterColor); accentLighterBrush.Freeze();
        app.Resources["AccentBackgroundLightColor"] = accentLighterColor;
        app.Resources["AccentBackgroundLightBrush"] = accentLighterBrush;

        // Destructive-action accent — Delete buttons, trash icons, "this is irreversible" cues.
        // Exposed as both Color (for SolidColorBrush x:Key overrides in scoped XAML) and Brush
        // (for direct DynamicResource consumers). User-tunable so a high-contrast theme doesn't
        // get stuck with a rust-red that fights its accent palette.
        var deleteBrush = new SolidColorBrush(_delete); deleteBrush.Freeze();
        var deleteHoverBrush = new SolidColorBrush(Lighten(_delete, 0.15)); deleteHoverBrush.Freeze();
        var deletePressedBrush = new SolidColorBrush(Lighten(_delete, -0.15)); deletePressedBrush.Freeze();
        app.Resources["AccentDeleteColor"] = _delete;
        app.Resources["AccentDeleteBrush"] = deleteBrush;
        app.Resources["AccentDeleteHoverBrush"] = deleteHoverBrush;
        app.Resources["AccentDeletePressedBrush"] = deletePressedBrush;

        // PaletteRed* override moved to AFTER ApplicationAccentColorManager.Apply below — that
        // call re-rebuilds parts of the WPF-UI palette (including PaletteRedBrush back to the
        // system red) so any override placed before it gets clobbered.

        // Accent dark — separate user-tunable colour used by surfaces that aren't the primary
        // accent but still want to follow the theme: launcher cells, inactive tab headers,
        // anywhere that needs an "ambient" accent-tinted dark. Also derive a slightly-lighter
        // shade for borders so the launcher gets a coherent tone with one user setting.
        var darkBorderBrush = new SolidColorBrush(Lighten(_dark, 0.15)); darkBorderBrush.Freeze();
        app.Resources["AccentBackgroundDarkColor"] = _dark;
        app.Resources["AccentBackgroundDarkBrush"] = darkBrush;
        app.Resources["AccentBackgroundDarkBorderBrush"] = darkBorderBrush;

        // Three neutral surface tones — Surface1 darkest (input bg / list rows), Surface2 the
        // standard window body, Surface3 elevated panels. XAML consumes via DynamicResource so
        // the user's theme tweaks apply live everywhere without re-instantiating templates.
        var surface1Brush = new SolidColorBrush(_surface1); surface1Brush.Freeze();
        var surface2Brush = new SolidColorBrush(_surface2); surface2Brush.Freeze();
        var surface3Brush = new SolidColorBrush(_surface3); surface3Brush.Freeze();
        app.Resources["Surface1Color"] = _surface1;
        app.Resources["Surface2Color"] = _surface2;
        app.Resources["Surface3Color"] = _surface3;
        app.Resources["Surface1Brush"] = surface1Brush;
        app.Resources["Surface2Brush"] = surface2Brush;
        app.Resources["Surface3Brush"] = surface3Brush;

        // Vertical gradient Surface1 (bottom) → Surface2 (top) — built imperatively because
        // DynamicResource on GradientStop.Color is broken in WPF: the parent LinearGradientBrush
        // gets frozen as part of resource caching, snapping the dynamic refs to whatever they
        // resolved to first (often only the first stop renders, the rest stay Transparent).
        // Re-allocating the whole brush on every theme change avoids the problem entirely and
        // costs nothing — Apply() runs only on Load / SetAsync / ResetAsync, not per-frame.
        var sidebarGradient = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 1),
            EndPoint = new System.Windows.Point(0, 0),
            GradientStops =
            {
                new GradientStop(_surface1, 0.0),
                new GradientStop(_surface2, 1.0),
            },
        };
        sidebarGradient.Freeze();
        app.Resources["SidebarGradientBrush"] = sidebarGradient;

        // Override WPF-UI v4's chrome brushes so the dark.baml defaults (#202020 window body,
        // #2D2D2D control fills) track our surface palette. WPF-UI's FluentWindow template
        // resolves Background via {DynamicResource WindowBackground}, NOT the FluentWindow's
        // own Background property — setting it inline in XAML is silently overridden by the
        // theme dictionary. ApplicationBackgroundBrush is also pinned in case some control
        // template still binds to the legacy v3 name.
        app.Resources["WindowBackground"] = surface2Brush;
        app.Resources["ApplicationBackgroundBrush"] = surface2Brush;
        // TextBox / RichTextBox / PasswordBox surfaces — the visible default-state fill is
        // TextControlBackground; the hover / focus / disabled variants share the same row
        // semantically so they all map onto Surface3 too (otherwise the row would flash a
        // different shade on pointer-over).
        app.Resources["TextControlBackground"] = surface3Brush;
        app.Resources["TextControlBackgroundPointerOver"] = surface3Brush;
        app.Resources["TextControlBackgroundFocused"] = surface3Brush;
        app.Resources["TextControlBackgroundDisabled"] = surface3Brush;

        // Focused border / underline accent for input controls. WPF-UI's TextBox + ComboBox
        // templates pick these up via StaticResource, which means they latch on at app start —
        // we have to replace the brush instance in App.Resources for live retheming to actually
        // repaint a focused field. Without this the input keeps the OLD accent border until
        // restart even though every other accent-using control has already updated.
        app.Resources["TextControlBorderBrushFocused"] = bgBrush;
        app.Resources["TextControlElevationBorderFocusedBrush"] = bgBrush;
        app.Resources["AccentControlElevationBorderBrush"] = bgBrush;

        // Default-appearance ui:Button surfaces — anything NOT marked Appearance=Primary/Danger
        // resolves Background through ButtonBackground. ComboBox / TimePicker / DatePicker
        // pickers reuse the same key family so they all track Surface3 in sync.
        app.Resources["ButtonBackground"] = surface3Brush;
        app.Resources["ButtonBackgroundPointerOver"] = surface3Brush;
        app.Resources["ButtonBackgroundPressed"] = surface3Brush;
        app.Resources["ButtonBackgroundDisabled"] = surface3Brush;
        app.Resources["ComboBoxBackground"] = surface3Brush;
        app.Resources["ComboBoxBackgroundPointerOver"] = surface3Brush;
        app.Resources["ComboBoxBackgroundFocused"] = surface3Brush;
        // ComboBox popup body — WPF-UI v4 uses ComboBoxDropDownBackground (extracted from the
        // packed dll). The earlier "ComboBoxPopupBackground" guess was wrong, which is why the
        // dropdown kept the dark default until now. Routed to Surface2 so the popup floats one
        // shade above the textbox face (Surface3) for a card-on-surface read.
        app.Resources["ComboBoxDropDownBackground"] = surface2Brush;
        app.Resources["ComboBoxItemBackground"] = surface2Brush;
        app.Resources["ComboBoxItemBackgroundPointerOver"] = surface3Brush;
        app.Resources["ComboBoxItemBackgroundSelected"] = surface3Brush;
        app.Resources["ComboBoxItemBackgroundSelectedPointerOver"] = surface3Brush;
        app.Resources["ComboBoxItemBackgroundSelectedDisabled"] = surface3Brush;
        app.Resources["ComboBoxItemBackgroundDisabled"] = surface2Brush;
        // ContextMenu chrome (right-click + tray menus). Same dll-string source as the ComboBox
        // family above. Routing to Surface2 keeps tray + right-click popups consistent with the
        // window body.
        app.Resources["ContextMenuBackground"] = surface2Brush;
        app.Resources["ContextMenuBorderBrush"] = darkBorderBrush;

        // WPF-UI MenuItem's submenu popup uses a SEPARATE brush key family — Flyout* — not
        // ContextMenu*. Verified by string-extracting Wpf.Ui.dll: the MenuItem template
        // resolves the popup chrome through FlyoutBackground / FlyoutBorderBrush /
        // SurfaceStrokeColorFlyoutBrush. Without these overrides the submenu kept the WPF-UI
        // dark.baml default while the parent menu (which DOES use ContextMenuBackground)
        // themed correctly. Routing to Surface3 so the submenu floats one shade above the
        // top-level menu's Surface2 surface.
        app.Resources["FlyoutBackground"] = surface3Brush;
        app.Resources["FlyoutBorderBrush"] = darkBorderBrush;
        app.Resources["SurfaceStrokeColorFlyoutBrush"] = darkBorderBrush;

        // SystemColors.* overrides at the App level — needed so submenu Popups (hosted in a
        // separate HwndSource) resolve them. The TrayIconService also adds them inside the
        // MenuItem Style.Resources scope, but Popup resource lookup is finicky and sometimes
        // skips the Style.Resources scope entirely; the App-level pin guarantees the chrome
        // brush is found regardless. Submenu chrome → Surface3 (one shade above the main menu
        // body), main menu chrome → Surface2 (set on the ContextMenu itself in TrayIconService).
        app.Resources[SystemColors.MenuBrushKey] = surface3Brush;
        app.Resources[SystemColors.MenuBarBrushKey] = surface2Brush;
        app.Resources[SystemColors.MenuTextBrushKey] = fgBrush;
        app.Resources[SystemColors.MenuHighlightBrushKey] = darkBrush;
        app.Resources[SystemColors.HighlightBrushKey] = darkBrush;
        app.Resources[SystemColors.HighlightTextBrushKey] = fgBrush;
        app.Resources[SystemColors.ControlBrushKey] = surface3Brush;
        app.Resources[SystemColors.ControlLightBrushKey] = surface3Brush;
        app.Resources[SystemColors.ControlDarkBrushKey] = surface2Brush;
        app.Resources[SystemColors.WindowBrushKey] = surface2Brush;
        app.Resources[SystemColors.WindowTextBrushKey] = fgBrush;
        app.Resources[SystemColors.GrayTextBrushKey] = fgDarkBrush;
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = darkBrush;
        app.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = fgBrush;
        // Generic Fluent v2 surface keys WPF-UI templates fall back to for popup chrome
        // (flyouts, autosuggest dropdowns, the date/time pickers' calendar). Routing them all
        // to Surface2 keeps every floating surface consistent with the window body.
        app.Resources["SolidBackgroundFillColorBaseBrush"] = surface2Brush;
        app.Resources["SolidBackgroundFillColorSecondaryBrush"] = surface2Brush;
        app.Resources["SolidBackgroundFillColorTertiaryBrush"] = surface3Brush;
        app.Resources["SolidBackgroundFillColorQuarternaryBrush"] = surface3Brush;
        app.Resources["AcrylicBackgroundFillColorDefaultBrush"] = surface2Brush;
        app.Resources["AcrylicBackgroundFillColorBaseBrush"] = surface2Brush;
        // Fluent v2 "layer" brushes — WPF-UI v4 uses these as the canonical popup chrome
        // (verified by string-extracting Wpf.Ui.dll). LayerFillColorDefaultBrush is what
        // ContextMenu / Flyout / AutoSuggest dropdowns resolve to when their explicit Bg key
        // doesn't override; pinning it to Surface2 makes the catch-all match the rest of the
        // popup family.
        app.Resources["LayerFillColorDefaultBrush"] = surface2Brush;
        app.Resources["LayerFillColorAltBrush"] = surface3Brush;
        app.Resources["LayerOnAcrylicFillColorDefaultBrush"] = surface2Brush;
        app.Resources["LayerOnAccentAcrylicFillColorDefaultBrush"] = surface2Brush;
        app.Resources["LayerOnMicaBaseAltFillColorDefaultBrush"] = surface2Brush;
        app.Resources["LayerOnMicaBaseAltFillColorSecondaryBrush"] = surface3Brush;

        // Dim subtext tone — captions, age / kind / source on clipboard rows, swatch
        // descriptions, etc. One global handle for "secondary text on dark surfaces".
        // (fgDarkBrush is hoisted at the top of the method for the SystemColors block.)
        app.Resources["AccentForegroundDarkColor"] = _fgDark;
        app.Resources["AccentForegroundDarkBrush"] = fgDarkBrush;

        // Re-key WPF-UI v4's body-text brushes to follow the user's foreground colours. Without
        // this, every TextBlock that doesn't set Foreground explicitly inherits the dark.baml
        // default (white) — invisible the moment the user picks light surfaces. Primary text
        // takes AccentForegroundLight (the same colour the user already chose for "text on
        // accent"); secondary / tertiary / disabled fall to AccentForegroundDark (the dim tone).
        // Same DynamicResource path the WPF-UI templates use, so the change propagates live.
        app.Resources["TextFillColorPrimaryBrush"] = fgBrush;
        app.Resources["TextFillColorSecondaryBrush"] = fgDarkBrush;
        app.Resources["TextFillColorTertiaryBrush"] = fgDarkBrush;
        app.Resources["TextFillColorDisabledBrush"] = fgDarkBrush;

        // TextBox / RichTextBox / PasswordBox glyph color — bound by template via
        // TextControlForeground (NOT TextFillColor*), so the hex inputs in the Theme tab kept
        // rendering white text on a light Surface3 even after the TextFillColor* overrides above.
        // All four states resolve to fgBrush so the typed text reads against the chosen surface
        // tone whatever theme the user picks.
        app.Resources["TextControlForeground"] = fgBrush;
        app.Resources["TextControlForegroundPointerOver"] = fgBrush;
        app.Resources["TextControlForegroundFocused"] = fgBrush;
        app.Resources["TextControlForegroundDisabled"] = fgDarkBrush;
        app.Resources["TextControlPlaceholderForeground"] = fgDarkBrush;
        app.Resources["TextControlPlaceholderForegroundPointerOver"] = fgDarkBrush;
        app.Resources["TextControlPlaceholderForegroundFocused"] = fgDarkBrush;
        app.Resources["TextControlPlaceholderForegroundDisabled"] = fgDarkBrush;

        // Default ui:Button label colour — same problem as TextBox: not a TextFillColor* consumer,
        // so the non-Primary sidebar buttons stayed white even when the user picked a light theme.
        // ButtonForeground is the WPF-UI v4 key the default Button template binds to via
        // DynamicResource; the four states track fgBrush except Disabled which dims to fgDarkBrush
        // so the affordance still reads as inactive.
        app.Resources["ButtonForeground"] = fgBrush;
        app.Resources["ButtonForegroundPointerOver"] = fgBrush;
        app.Resources["ButtonForegroundPressed"] = fgBrush;
        app.Resources["ButtonForegroundDisabled"] = fgDarkBrush;

        // ComboBox label / chosen-item glyph color — same family of overrides for the closed
        // picker face. The dropdown items inside the popup are styled separately
        // (ComboBoxItemForeground) but they currently inherit through TextFillColorPrimaryBrush
        // already, so just the closed-state face needs the explicit nudge.
        app.Resources["ComboBoxForeground"] = fgBrush;
        app.Resources["ComboBoxForegroundPointerOver"] = fgBrush;
        app.Resources["ComboBoxForegroundFocused"] = fgBrush;
        app.Resources["ComboBoxForegroundDisabled"] = fgDarkBrush;
        app.Resources["ComboBoxItemForeground"] = fgBrush;
        app.Resources["ComboBoxItemForegroundPointerOver"] = fgBrush;
        app.Resources["ComboBoxItemForegroundSelected"] = fgBrush;
        app.Resources["ComboBoxItemForegroundSelectedPointerOver"] = fgBrush;

        // NavigationView sidebar item label — selected items use AccentForegroundLight (same as
        // every other accent-on-accent surface — it's the colour the user already picked for
        // "text on accent"), unselected items follow the standard fgBrush so they stay readable
        // on the chrome surface.
        app.Resources["NavigationViewItemForeground"] = fgBrush;
        app.Resources["NavigationViewItemForegroundPointerOver"] = fgBrush;
        app.Resources["NavigationViewItemForegroundPressed"] = fgBrush;
        app.Resources["NavigationViewItemForegroundSelected"] = fgBrush;
        app.Resources["NavigationViewItemForegroundSelectedPointerOver"] = fgBrush;
        app.Resources["NavigationViewItemForegroundSelectedPressed"] = fgBrush;
        app.Resources["NavigationViewItemForegroundDisabled"] = fgDarkBrush;

        // ToggleSwitch knob / off-state stroke — dark.baml binds them to ControlStrokeColor*
        // which keeps a near-white tone; without the override the switch knob disappears against
        // a light surface. Routing them to fgDarkBrush gives a visible, theme-consistent edge.
        app.Resources["ControlStrongFillColorDefaultBrush"] = fgDarkBrush;
        app.Resources["ControlStrongStrokeColorDefaultBrush"] = fgDarkBrush;

        // Slider track + tick + outer-thumb-ring → fgDarkBrush. WPF-UI v4 leaves these on
        // near-white defaults (verified by string-extracting Wpf.Ui.dll for keys
        // `SliderTrackFill`, `SliderTickBarFill`, `SliderOuterThumbBackground`), which read
        // as "always white" against the editor / Trace / BgRemover panels regardless of the
        // user's theme. Routing them to the dim foreground tone matches the rest of the
        // secondary-text family while keeping the inner thumb fill on accent so the
        // slider's identity colour still pops. Pointer-over of the track gets the same
        // dim tone (no hover-darken) — the hover affordance lives on the thumb instead.
        app.Resources["SliderTrackFill"] = fgDarkBrush;
        app.Resources["SliderTrackFillPointerOver"] = fgDarkBrush;
        app.Resources["SliderTickBarFill"] = fgDarkBrush;
        app.Resources["SliderOuterThumbBackground"] = fgDarkBrush;

        // WPF-UI v4 declares BOTH naming conventions in resources/accent.baml — the legacy
        // SystemAccentColor* family AND the Fluent-v2 AccentFillColor* family. Different
        // controls bind to different ones (Button → AccentFillColor*, ToggleSwitch internal
        // tracks → SystemAccentColor*). We override both so every accent-aware surface follows
        // the user's choice. Replacing the BRUSH objects (not just Color keys) because v4's
        // theme XAML uses StaticResource Color="{...}" inside its brush definitions — changing
        // only color keys would leave already-instantiated brushes pointing at stale colors.
        app.Resources["SystemAccentColor"] = _bg;
        app.Resources["SystemAccentColorPrimary"] = _bg;
        app.Resources["SystemAccentColorSecondary"] = Lighten(_bg, 0.15);
        app.Resources["SystemAccentColorTertiary"] = Lighten(_bg, -0.15);
        app.Resources["SystemAccentBrush"] = bgBrush;
        app.Resources["SystemAccentColorBrush"] = bgBrush;
        app.Resources["SystemAccentColorPrimaryBrush"] = bgBrush;
        app.Resources["SystemAccentColorSecondaryBrush"] = lighterBrush;
        app.Resources["SystemAccentColorTertiaryBrush"] = darkerBrush;

        // Fluent v2 base accent fill keys — these feed the per-control intermediary brushes below.
        app.Resources["AccentFillColorDefaultBrush"] = bgBrush;
        app.Resources["AccentFillColorSecondaryBrush"] = lighterBrush;
        app.Resources["AccentFillColorTertiaryBrush"] = darkerBrush;
        app.Resources["AccentFillColorDisabledBrush"] = darkerBrush;
        app.Resources["AccentFillColorSelectedTextBackgroundBrush"] = bgBrush;
        app.Resources["AccentTextFillColorPrimaryBrush"] = bgBrush;
        app.Resources["AccentTextFillColorSecondaryBrush"] = bgBrush;
        app.Resources["AccentTextFillColorTertiaryBrush"] = bgBrush;
        app.Resources["TextOnAccentFillColorPrimaryBrush"] = fgBrush;
        app.Resources["TextOnAccentFillColorSecondaryBrush"] = fgBrush;
        app.Resources["TextOnAccentFillColorDisabledBrush"] = fgBrush;
        app.Resources["TextOnAccentFillColorSelectedTextBrush"] = fgBrush;

        // Per-control intermediary brushes — extracted from WPF-UI v4 dark.baml. Templates bind
        // to THESE names, not directly to AccentFillColor*. They're declared in the theme
        // dictionary as separate brush instances pointing at AccentFillColor* via StaticResource —
        // so changing AccentFillColor* alone leaves these intermediaries stuck on the original
        // accent. Override at App.Resources level so DynamicResource consumers (the templates)
        // resolve to our brushes instead of the merged-dictionary originals.
        app.Resources["AccentButtonBackground"] = bgBrush;
        app.Resources["AccentButtonBackgroundPointerOver"] = lighterBrush;
        app.Resources["AccentButtonBackgroundPressed"] = darkerBrush;
        app.Resources["AccentButtonBorderBrushPressed"] = darkerBrush;
        app.Resources["AccentButtonForeground"] = fgBrush;
        app.Resources["AccentButtonForegroundPointerOver"] = fgBrush;
        app.Resources["AccentButtonForegroundPressed"] = fgBrush;

        // ToggleSwitch ON-state track — this is what changes when a switch toggles to ON.
        app.Resources["ToggleSwitchFillOn"] = bgBrush;
        app.Resources["ToggleSwitchFillOnPointerOver"] = lighterBrush;
        app.Resources["ToggleSwitchFillOnDisabled"] = darkerBrush;

        // CheckBox checked state — same pattern that the Uploaders DataTemplate uses locally
        // (verified by string-extracting Wpf.Ui.dll): dark accent for the fill, the lighter
        // accent foreground for both the border and the tick glyph. Promoted to a global
        // override so every <CheckBox> in the app picks up the "uploaders look" without each
        // page having to repeat the four-key dictionary. Stroke* keys are NOT what wpf-ui v4
        // binds to — border is CheckBoxCheckBorderBrush and the tick is CheckBoxCheckGlyphForeground.
        app.Resources["CheckBoxCheckBackgroundFillChecked"] = darkBrush;
        app.Resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = darkBrush;
        app.Resources["CheckBoxCheckBackgroundFillCheckedPressed"] = darkBrush;
        app.Resources["CheckBoxCheckBorderBrush"] = fgBrush;
        app.Resources["CheckBoxCheckBorderBrushChecked"] = fgBrush;
        app.Resources["CheckBoxCheckBorderBrushCheckedPointerOver"] = fgBrush;
        app.Resources["CheckBoxCheckBorderBrushCheckedPressed"] = fgBrush;
        app.Resources["CheckBoxCheckGlyphForeground"] = fgBrush;
        app.Resources["CheckBoxCheckGlyphForegroundChecked"] = fgBrush;
        app.Resources["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = fgBrush;
        app.Resources["CheckBoxCheckGlyphForegroundCheckedPressed"] = fgBrush;

        // Slider thumb + filled track — same rationale as CheckBox: the thumb is an accent dot
        // that needs to read clearly against the surface, and the lighter accent vanishes
        // against Surface3 backgrounds. Cover the standard / hover / pressed / disabled set
        // because wpf-ui's slider template flips between these on interaction.
        app.Resources["SliderThumbBackground"] = darkBrush;
        app.Resources["SliderThumbBackgroundPointerOver"] = darkBrush;
        app.Resources["SliderThumbBackgroundPressed"] = darkBrush;
        app.Resources["SliderThumbBackgroundDisabled"] = darkerBrush;
        app.Resources["SliderTrackValueFill"] = darkBrush;
        app.Resources["SliderTrackValueFillPointerOver"] = darkBrush;
        app.Resources["SliderTrackValueFillPressed"] = darkBrush;
        app.Resources["SliderTrackValueFillDisabled"] = darkerBrush;

        // NavigationView selected item background — sidebar selection in MainWindow.
        app.Resources["NavigationViewItemBackgroundSelected"] = bgBrush;
        app.Resources["NavigationViewItemBackgroundSelectedLeftFluent"] = bgBrush;
        app.Resources["NavigationViewSelectionIndicatorForeground"] = bgBrush;

        // Apply through WPF-UI's manager too — repaints SystemAccentColor* in case anything else
        // reads those directly. Pure write, no template re-creation, so it doesn't undo our
        // overrides above.
        ApplicationAccentColorManager.Apply(_bg, ApplicationTheme.Dark, false);

        // Danger / red palette override — MUST sit after ApplicationAccentColorManager.Apply,
        // which otherwise resets PaletteRedBrush back to the WPF-UI default system red. v4's
        // Button.xaml template wires Appearance="Danger" to PaletteRedBrush via DynamicResource,
        // so re-keying it here retints every Danger button across the app to the user's
        // accent-delete colour. PaletteRedColor kept in sync for any consumer that pulls the raw
        // colour rather than the brush.
        app.Resources["PaletteRedBrush"] = deleteBrush;
        app.Resources["PaletteRedColor"] = _delete;
    }

    /// <summary>Mix toward white (positive amount) or black (negative amount). Used for the
    /// Primary→Secondary→Tertiary accent ladder.</summary>
    private static Color Lighten(Color c, double amount)
    {
        var t = (byte)Math.Clamp(amount > 0 ? 255 : 0, 0, 255);
        var w = Math.Abs(amount);
        byte Mix(byte channel) => (byte)Math.Clamp(channel * (1 - w) + t * w, 0, 255);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private static Color? TryParseHex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        try { return (Color)ColorConverter.ConvertFromString(s)!; }
        catch { return null; }
    }

    public static string ToHex(Color c)
        => $"#{c.R.ToString("X2", CultureInfo.InvariantCulture)}{c.G.ToString("X2", CultureInfo.InvariantCulture)}{c.B.ToString("X2", CultureInfo.InvariantCulture)}";
}
