using System.Globalization;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Wormholes;

/// <summary>App-wide default for the two per-wormhole-overridable visual knobs: icon-tile
/// pixel size and window opacity. Each wormhole record can override either independently;
/// when it doesn't, the live window reads from this service. Hosted as a singleton so the
/// manager + Settings VM share the same instance and a slider drag in the Wormholes tab
/// propagates live to every open wormhole via <see cref="DefaultsChanged"/>.
///
/// Persistence is delegated to <see cref="ISettingsStore"/>. Two keys:
/// <see cref="IconSizeKey"/> (int, 0 = "use Windows desktop icon size") and
/// <see cref="OpacityKey"/> (double, clamped 0.30–1.00 — fully transparent isn't a useful
/// state and would make the wormhole un-clickable).</summary>
public sealed class WormholeDefaultsService
{
    public const string IconSizeKey        = "app.wormholes.default_icon_size_px";
    public const string OpacityKey         = "app.wormholes.default_opacity";
    public const string BorderOpacityKey   = "app.wormholes.default_border_opacity";
    public const string TilePaddingKey     = "app.wormholes.default_tile_padding_px";
    public const string LineSpacingKey     = "app.wormholes.default_line_spacing_px";
    public const string LabelFontSizeKey   = "app.wormholes.default_label_font_size_px";
    public const string LabelMaxLinesKey   = "app.wormholes.default_label_max_lines";
    public const string AutoDisableTopmostOnLaunchKey = "app.wormholes.auto_disable_topmost_on_launch";
    public const string WebLinkFaviconsKey = "app.wormholes.web_link_favicons";
    public const string KeepVisibleOnShowDesktopKey = "app.wormholes.keep_visible_on_show_desktop";
    public const string HideHeaderChromeKey = "app.wormholes.hide_header_chrome";
    public const string SnapToGridKey       = "app.wormholes.snap_to_grid";
    public const string SnapGridSizeKey     = "app.wormholes.snap_grid_size_px";
    public const string SnapToWormholesKey  = "app.wormholes.snap_to_wormholes";
    public const string SnapToScreenEdgesKey = "app.wormholes.snap_to_screen_edges";
    public const string SnapGapKey          = "app.wormholes.snap_gap_px";

    private const int IconMin = 0;     // 0 has the special meaning "use DesktopIconSize.Get()"
    private const int IconMax = 256;
    // OpacityMin = 0.01: 1 % is the floor. A fully transparent backdrop (0 %) would make the
    // header strip un-hittable for the header double-click → roll-up gesture (mouse events pass
    // through the alpha-0 pixels straight to the desktop / window below). 1 % keeps the strip
    // hit-testable while staying visually invisible-enough for the "icons floating" look.
    private const double OpacityMin = 0.01;
    private const double OpacityMax = 1.00;
    private const double OpacityFallback = 0.70;
    // Border opacity controls the OuterFrame's 1 px accent ring + the drop shadow underneath.
    // Independent from the body/header backdrop opacity so the user can pick e.g. "ghost-tile"
    // mode = backdrop 1 %, border 100 % (icons hover with a visible frame) or vice versa.
    // Minimum 0: the ring + shadow can disappear completely without breaking hit-test (the
    // OuterFrame's Background="Transparent" still receives mouse events).
    private const double BorderOpacityMin = 0.00;
    private const double BorderOpacityMax = 1.00;
    private const double BorderOpacityFallback = 1.00;
    private const int TilePaddingMin = 0;
    private const int TilePaddingMax = 32;
    private const int TilePaddingFallback = 4;
    // LineSpacingPx now behaves like a CSS negative-margin: applied as the tile's bottom
    // Margin so adjacent rows can OVERLAP visually without altering the label area itself.
    // Negative → rows overlap by that many px; positive → extra gap between rows.
    private const int LineSpacingMin = -32;
    private const int LineSpacingMax = 32;
    private const int LineSpacingFallback = -4;
    // Label font size + max lines: control how much vertical room the label gets. Together
    // they determine TileHeight's text portion (lineHeight × maxLines + TextBlock margin).
    private const int LabelFontSizeMin = 8;
    private const int LabelFontSizeMax = 20;
    private const int LabelFontSizeFallback = 12;
    private const int LabelMaxLinesMin = 1;
    private const int LabelMaxLinesMax = 3;
    private const int LabelMaxLinesFallback = 2;
    // Snap grid: 2 px is the smallest step that still reads as a grid, 256 covers "quarter of a
    // 1080p screen" style coarse layouts. 16 is the default step — matches the tile rhythm.
    private const int SnapGridMin = 2;
    private const int SnapGridMax = 256;
    private const int SnapGridFallback = 16;
    // Gap kept between a snapped wormhole and its neighbour / the screen edge. 0 = flush.
    private const int SnapGapMin = 0;
    private const int SnapGapMax = 200;
    private const int SnapGapFallback = 0;

    private readonly ISettingsStore _store;
    private readonly ILogger<WormholeDefaultsService> _logger;
    private int _defaultIconSizePx;
    private double _defaultOpacity = OpacityFallback;
    private double _defaultBorderOpacity = BorderOpacityFallback;
    private int _defaultTilePaddingPx = TilePaddingFallback;
    private int _defaultLineSpacingPx = LineSpacingFallback;
    private int _defaultLabelFontSizePx = LabelFontSizeFallback;
    private int _defaultLabelMaxLines = LabelMaxLinesFallback;
    // Default ON: when wormholes ride on top of a fullscreen app via the toggle hotkey, the
    // natural follow-up is "click an icon to launch something, the launched app should win the
    // foreground". The auto-disable + post-launch backtrack flow in WormholeWindow is what
    // delivers that — opt-in would just hide the feature from users who'd benefit by default.
    private bool _autoDisableTopmostOnLaunch = true;
    // Default ON: Windows never fetches real site favicons for .url links, so showing them is a
    // strict visual upgrade. Off-switch exists for users who'd rather not make network calls or
    // have their .url files stamped with IconFile=.
    private bool _webLinkFaviconsEnabled = true;
    // Default ON: a wormhole is a desktop widget, and "Show desktop" is the user asking to SEE
    // the desktop — hiding the widgets that live on it is the opposite of what they meant. The
    // off-switch restores the plain top-level-window behaviour (minimizes with everything else).
    private bool _keepVisibleOnShowDesktop = true;
    private bool _hideHeaderChrome;
    private bool _snapToGrid;
    private int _snapGridSizePx = SnapGridFallback;
    private bool _snapToWormholes;
    private bool _snapToScreenEdges;
    private int _snapGapPx = SnapGapFallback;

    public WormholeDefaultsService(ISettingsStore store, ILogger<WormholeDefaultsService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Effective icon-tile size to use when a record has no per-wormhole override
    /// (<see cref="WormholeRecord.IconSizePx"/> == 0). 0 still means "fall back further" — to
    /// <see cref="DesktopIconSize.Get()"/> — so the user's Windows desktop preference wins
    /// when neither the wormhole nor the app has an explicit value.</summary>
    public int DefaultIconSizePx => _defaultIconSizePx;

    /// <summary>Effective body+header backdrop opacity. Pre-clamped to the legal range.</summary>
    public double DefaultOpacity => _defaultOpacity;

    /// <summary>Effective opacity for the OuterFrame's 1 px accent ring + its drop shadow.
    /// Independent from the body opacity above — drives <see cref="WormholeWindow.ApplyAppearance"/>
    /// which sets the ring's BorderBrush and modulates the shadow's effect opacity.</summary>
    public double DefaultBorderOpacity => _defaultBorderOpacity;

    /// <summary>Extra pixels added around each item tile beyond the icon size — controls how
    /// dense the grid feels. 0 = icons hug each other (Portals-style tight pack); 32 = wide
    /// breathing room. Drives <see cref="WormholeItemViewModel.TileWidth"/>,
    /// <see cref="WormholeItemViewModel.TileHeight"/> and the icon Margin.</summary>
    public int DefaultTilePaddingPx => _defaultTilePaddingPx;

    /// <summary>Margin verticale applicato fra righe di tile come "CSS negative margin": valori
    /// negativi fanno OVERLAP delle righe (la riga sotto sale sopra la label di quella sopra)
    /// senza modificare l'altezza della label; positivi aggiungono gap. Non influenza il numero
    /// di righe di testo (controllato da <see cref="DefaultLabelMaxLines"/>).
    /// Drives <see cref="WormholeItemViewModel.TileMargin"/>.</summary>
    public int DefaultLineSpacingPx => _defaultLineSpacingPx;

    /// <summary>Pixel del font della label sotto l'icona. Rendering Segoe UI; range 8-20.
    /// Drives <see cref="WormholeItemViewModel.LabelFontSizePx"/> e via lui la
    /// <see cref="WormholeItemViewModel.TileHeight"/>.</summary>
    public int DefaultLabelFontSizePx => _defaultLabelFontSizePx;

    /// <summary>Numero massimo di righe di testo nella label. 1 = ellipsis sempre, 2 = wrap
    /// fino a 2 righe (default), 3 = wrap fino a 3 righe. Determina, insieme al font size,
    /// l'altezza riservata alla label nella tile.</summary>
    public int DefaultLabelMaxLines => _defaultLabelMaxLines;

    /// <summary>When true, launching anything from a wormhole (double-click on a tile,
    /// Enter on a selection, middle-click → open source folder, hamburger → open folder,
    /// drop file onto an executable tile) automatically toggles every wormhole's Topmost
    /// flag back to OFF — sending them behind the foreground app so the user can interact
    /// with the program they just launched without alt-tabbing or pressing the toggle a
    /// second time. Default false to preserve the explicit-toggle-only behaviour for users
    /// who want the wormholes to stay above the launched app.</summary>
    public bool AutoDisableTopmostOnLaunch => _autoDisableTopmostOnLaunch;

    /// <summary>When true, web links (<c>.url</c> files) in wormholes fetch and display the real
    /// site favicon (and stamp it into the <c>.url</c> so it shows in Explorer too). Default true.
    /// See <see cref="Favicons.FaviconService"/>.</summary>
    public bool WebLinkFaviconsEnabled => _webLinkFaviconsEnabled;

    /// <summary>When true, "Show desktop" (Win+D, the taskbar's far-corner button, Win+M …) no
    /// longer takes the wormholes down with every other window: the minimize is swallowed and a
    /// hide request from the shell is vetoed, so the widgets stay put on the revealed desktop.
    /// Read on demand by each live window — no event needed.</summary>
    public bool KeepVisibleOnShowDesktop => _keepVisibleOnShowDesktop;

    /// <summary>When true, the header strip of every wormhole renders empty: title, chevron,
    /// search, lock and hamburger fade out and the strip's own backdrop goes transparent. The
    /// 32 px row stays reserved (so the layout doesn't jump and the strip remains draggable);
    /// moving the mouse over the wormhole brings the chrome back.</summary>
    public bool HideHeaderChrome => _hideHeaderChrome;

    /// <summary>Snap a dragged / resized wormhole's edges to a <see cref="SnapGridSizePx"/> grid.</summary>
    public bool SnapToGrid => _snapToGrid;

    /// <summary>Grid step in physical pixels used when <see cref="SnapToGrid"/> is on.</summary>
    public int SnapGridSizePx => _snapGridSizePx;

    /// <summary>Snap a dragged / resized wormhole's edges to the edges (and centre lines) of the
    /// other open wormholes.</summary>
    public bool SnapToWormholes => _snapToWormholes;

    /// <summary>Snap a dragged / resized wormhole to the work area of the monitor it's on.</summary>
    public bool SnapToScreenEdges => _snapToScreenEdges;

    /// <summary>Gap in physical pixels always left between a snapped wormhole and whatever it
    /// snapped against (another wormhole's edge, or the screen edge). 0 = flush contact.</summary>
    public int SnapGapPx => _snapGapPx;

    /// <summary>Raised when the header-chrome visibility default changed, so every live window
    /// re-applies it without a restart.</summary>
    public event EventHandler? HeaderChromeChanged;

    /// <summary>Raised when the "keep visible on Show desktop" default changed. Live windows
    /// join or leave the desktop's z-order group in response (see <see cref="DesktopOwnership"/>).</summary>
    public event EventHandler? KeepVisibleOnShowDesktopChanged;

    /// <summary>Raised when the default icon size changed. Subscribers must re-extract icons
    /// at the new size (expensive — IShellItemImageFactory call per item).</summary>
    public event EventHandler? IconSizeChanged;

    /// <summary>Raised when the default opacity changed. Cheap subscribers — just update
    /// <c>OuterFrame.Opacity</c> on each open wormhole, no icon re-extraction. Separating
    /// this from <see cref="IconSizeChanged"/> is what keeps the opacity slider drag fluid
    /// (otherwise every value tick would rebuild every wormhole's item list).</summary>
    public event EventHandler? OpacityChanged;

    /// <summary>Raised when the border opacity changed. Same lightweight handling as
    /// <see cref="OpacityChanged"/> — re-apply the appearance on every live window.</summary>
    public event EventHandler? BorderOpacityChanged;

    /// <summary>Raised when the default tile padding changed. Subscribers rebuild the item
    /// VMs (cheap — cached icons reused since the icon size didn't change).</summary>
    public event EventHandler? TilePaddingChanged;

    /// <summary>Raised when the default line spacing changed. Same cost profile as
    /// <see cref="TilePaddingChanged"/> — item VMs rebuilt, icons cache-hit.</summary>
    public event EventHandler? LineSpacingChanged;

    /// <summary>Raised when label font size changed. Same handling as TilePadding/LineSpacing —
    /// item VMs rebuilt (TileHeight depends on label font).</summary>
    public event EventHandler? LabelFontSizeChanged;

    /// <summary>Raised when label max lines changed. Triggers VM rebuild (TileHeight reflects
    /// the new label area).</summary>
    public event EventHandler? LabelMaxLinesChanged;

    /// <summary>Raised when the web-link favicons toggle changed. The manager rebuilds live item
    /// lists so flipping it ON fetches favicons immediately (and flipping OFF stops further
    /// fetches; already-stamped <c>.url</c> files keep their icon).</summary>
    public event EventHandler? WebLinkFaviconsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var iconRaw = await _store.GetAsync(IconSizeKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(iconRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var icon))
                _defaultIconSizePx = Math.Clamp(icon, IconMin, IconMax);

            var opacityRaw = await _store.GetAsync(OpacityKey, cancellationToken).ConfigureAwait(false);
            if (double.TryParse(opacityRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var op))
                _defaultOpacity = Math.Clamp(op, OpacityMin, OpacityMax);

            var borderRaw = await _store.GetAsync(BorderOpacityKey, cancellationToken).ConfigureAwait(false);
            if (double.TryParse(borderRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var bo))
                _defaultBorderOpacity = Math.Clamp(bo, BorderOpacityMin, BorderOpacityMax);

            var padRaw = await _store.GetAsync(TilePaddingKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(padRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pad))
                _defaultTilePaddingPx = Math.Clamp(pad, TilePaddingMin, TilePaddingMax);

            var lineRaw = await _store.GetAsync(LineSpacingKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(lineRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
                _defaultLineSpacingPx = Math.Clamp(line, LineSpacingMin, LineSpacingMax);

            var fontRaw = await _store.GetAsync(LabelFontSizeKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(fontRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var font))
                _defaultLabelFontSizePx = Math.Clamp(font, LabelFontSizeMin, LabelFontSizeMax);

            var maxRaw = await _store.GetAsync(LabelMaxLinesKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(maxRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
                _defaultLabelMaxLines = Math.Clamp(max, LabelMaxLinesMin, LabelMaxLinesMax);

            var autoTopmostRaw = await _store.GetAsync(AutoDisableTopmostOnLaunchKey, cancellationToken).ConfigureAwait(false);
            if (bool.TryParse(autoTopmostRaw, out var autoTopmost))
                _autoDisableTopmostOnLaunch = autoTopmost;

            var faviconsRaw = await _store.GetAsync(WebLinkFaviconsKey, cancellationToken).ConfigureAwait(false);
            if (bool.TryParse(faviconsRaw, out var favicons))
                _webLinkFaviconsEnabled = favicons;

            var keepVisibleRaw = await _store.GetAsync(KeepVisibleOnShowDesktopKey, cancellationToken).ConfigureAwait(false);
            if (bool.TryParse(keepVisibleRaw, out var keepVisible))
                _keepVisibleOnShowDesktop = keepVisible;

            var hideHeaderRaw = await _store.GetAsync(HideHeaderChromeKey, cancellationToken).ConfigureAwait(false);
            if (bool.TryParse(hideHeaderRaw, out var hideHeader))
                _hideHeaderChrome = hideHeader;

            var snapGridRaw = await _store.GetAsync(SnapToGridKey, cancellationToken).ConfigureAwait(false);
            if (bool.TryParse(snapGridRaw, out var snapGrid))
                _snapToGrid = snapGrid;

            var gridSizeRaw = await _store.GetAsync(SnapGridSizeKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(gridSizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gridSize))
                _snapGridSizePx = Math.Clamp(gridSize, SnapGridMin, SnapGridMax);

            var snapWormholesRaw = await _store.GetAsync(SnapToWormholesKey, cancellationToken).ConfigureAwait(false);
            if (bool.TryParse(snapWormholesRaw, out var snapWormholes))
                _snapToWormholes = snapWormholes;

            var snapEdgesRaw = await _store.GetAsync(SnapToScreenEdgesKey, cancellationToken).ConfigureAwait(false);
            if (bool.TryParse(snapEdgesRaw, out var snapEdges))
                _snapToScreenEdges = snapEdges;

            var gapRaw = await _store.GetAsync(SnapGapKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(gapRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gap))
                _snapGapPx = Math.Clamp(gap, SnapGapMin, SnapGapMax);
        }
        catch (Exception ex)
        {
            // Don't let a settings-store hiccup crash module init — defaults stay at fallback.
            _logger.LogWarning(ex, "WormholeDefaultsService load failed; using built-in defaults");
        }
    }

    public async Task SetDefaultTilePaddingAsync(int paddingPx, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(paddingPx, TilePaddingMin, TilePaddingMax);
        if (clamped == _defaultTilePaddingPx) return;
        _defaultTilePaddingPx = clamped;
        await _store.SetAsync(TilePaddingKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        TilePaddingChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDefaultLineSpacingAsync(int spacingPx, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(spacingPx, LineSpacingMin, LineSpacingMax);
        if (clamped == _defaultLineSpacingPx) return;
        _defaultLineSpacingPx = clamped;
        await _store.SetAsync(LineSpacingKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        LineSpacingChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDefaultLabelFontSizeAsync(int fontSizePx, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(fontSizePx, LabelFontSizeMin, LabelFontSizeMax);
        if (clamped == _defaultLabelFontSizePx) return;
        _defaultLabelFontSizePx = clamped;
        await _store.SetAsync(LabelFontSizeKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        LabelFontSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDefaultLabelMaxLinesAsync(int maxLines, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(maxLines, LabelMaxLinesMin, LabelMaxLinesMax);
        if (clamped == _defaultLabelMaxLines) return;
        _defaultLabelMaxLines = clamped;
        await _store.SetAsync(LabelMaxLinesKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        LabelMaxLinesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDefaultIconSizeAsync(int sizePx, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(sizePx, IconMin, IconMax);
        if (clamped == _defaultIconSizePx) return;
        _defaultIconSizePx = clamped;
        await _store.SetAsync(IconSizeKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        IconSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDefaultOpacityAsync(double opacity, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(opacity, OpacityMin, OpacityMax);
        if (Math.Abs(clamped - _defaultOpacity) < 0.005) return;
        _defaultOpacity = clamped;
        await _store.SetAsync(OpacityKey, clamped.ToString("F2", CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        OpacityChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDefaultBorderOpacityAsync(double opacity, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(opacity, BorderOpacityMin, BorderOpacityMax);
        if (Math.Abs(clamped - _defaultBorderOpacity) < 0.005) return;
        _defaultBorderOpacity = clamped;
        await _store.SetAsync(BorderOpacityKey, clamped.ToString("F2", CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        BorderOpacityChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetAutoDisableTopmostOnLaunchAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == _autoDisableTopmostOnLaunch) return;
        _autoDisableTopmostOnLaunch = enabled;
        await _store.SetAsync(AutoDisableTopmostOnLaunchKey,
            enabled.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        // No event raised — the flag is read on demand at launch time (see
        // WormholeWindow.MaybeAutoDisableTopmost), no live UI reflects it.
    }

    public async Task SetKeepVisibleOnShowDesktopAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == _keepVisibleOnShowDesktop) return;
        _keepVisibleOnShowDesktop = enabled;
        await _store.SetAsync(KeepVisibleOnShowDesktopKey, enabled.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        KeepVisibleOnShowDesktopChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetHideHeaderChromeAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == _hideHeaderChrome) return;
        _hideHeaderChrome = enabled;
        await _store.SetAsync(HideHeaderChromeKey, enabled.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        HeaderChromeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetSnapToGridAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == _snapToGrid) return;
        _snapToGrid = enabled;
        await _store.SetAsync(SnapToGridKey, enabled.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetSnapGridSizeAsync(int sizePx, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(sizePx, SnapGridMin, SnapGridMax);
        if (clamped == _snapGridSizePx) return;
        _snapGridSizePx = clamped;
        await _store.SetAsync(SnapGridSizeKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetSnapToWormholesAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == _snapToWormholes) return;
        _snapToWormholes = enabled;
        await _store.SetAsync(SnapToWormholesKey, enabled.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetSnapToScreenEdgesAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == _snapToScreenEdges) return;
        _snapToScreenEdges = enabled;
        await _store.SetAsync(SnapToScreenEdgesKey, enabled.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetSnapGapAsync(int gapPx, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(gapPx, SnapGapMin, SnapGapMax);
        if (clamped == _snapGapPx) return;
        _snapGapPx = clamped;
        await _store.SetAsync(SnapGapKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetWebLinkFaviconsEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == _webLinkFaviconsEnabled) return;
        _webLinkFaviconsEnabled = enabled;
        await _store.SetAsync(WebLinkFaviconsKey,
            enabled.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
        WebLinkFaviconsChanged?.Invoke(this, EventArgs.Empty);
    }
}
