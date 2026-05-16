using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.App.Services.Launcher;

namespace AresToys.App.Services.Wormholes;

/// <summary>UI wrapper around a single visible entry in a wormhole — the path of a real file
/// or folder inside the watched source folder, plus its rendered icon and label. Created
/// transiently on every refresh tick of the parent <see cref="WormholeWindow"/> (or on every
/// FileSystemWatcher debounced burst) — no per-item JSON state.</summary>
public sealed partial class WormholeItemViewModel : ObservableObject
{
    public string AbsolutePath { get; }

    /// <summary>Tile icon pixel size — resolved at construction time from the wormhole's
    /// per-record zoom (which falls back to the system desktop icon size when unset). Bound
    /// directly to the Image's Width/Height in the XAML so the rendered icon is pixel-accurate
    /// for whatever size we asked <see cref="IconService.GetIconAtSize"/> to fetch — no WPF
    /// resampling between request size and render size.</summary>
    public int IconSizePx { get; }

    /// <summary>User-tunable extra space around the icon inside the tile. Smaller = denser
    /// (Portals-style), larger = airier. Controls vertical + horizontal padding identically
    /// (one knob is plenty; separate H/V would just be analysis paralysis).</summary>
    public int TilePaddingPx { get; }

    /// <summary>Line spacing applied as a "CSS negative-margin"-style effect on the tile:
    /// outputs as <see cref="TileMargin"/> bottom value. Negative pulls the next row's tile up,
    /// causing visual overlap (icon-on-label) without altering the text area; positive expands
    /// the gap. NEVER affects how many lines the label gets — that's <see cref="LabelMaxLines"/>.</summary>
    public int LineSpacingPx { get; }

    /// <summary>Pixel font size of the label TextBlock under the icon. Drives both the
    /// rendered glyph height AND the reserved label-area height (see <see cref="TileHeight"/>).</summary>
    public int LabelFontSizePx { get; }

    /// <summary>Max wrapped lines the label can use before being ellipsized. Together with
    /// <see cref="LabelFontSizePx"/> this sets the label area's height (and therefore TileHeight).</summary>
    public int LabelMaxLines { get; }

    /// <summary>Approximated line height for the chosen font size. Segoe UI's natural line
    /// spacing is ~1.36 × the EM size; rounding up keeps the wrap from clipping a final pixel
    /// off the last glyph. Public so XAML can bind <c>TextBlock.MaxHeight</c> to a multiple of it.</summary>
    public double LabelLineHeight => Math.Ceiling(LabelFontSizePx * 1.36);

    /// <summary>Reserved label area height — exactly enough for <see cref="LabelMaxLines"/>
    /// of Segoe UI at <see cref="LabelFontSizePx"/>. Bound to <c>TextBlock.MaxHeight</c> in the
    /// DataTemplate so WPF wraps to at most that many lines, then ellipsizes.</summary>
    public double LabelAreaHeight => LabelLineHeight * LabelMaxLines;

    /// <summary>Container tile dimensions. Horizontal: icon size plus a small horizontal
    /// breathing room (+8 baseline) plus per-side TilePadding. Vertical: icon row
    /// (IconSize + 4 + TilePaddingPx of IconMargin) + a 2-px gutter + label area
    /// (LineHeight × MaxLines) + the TextBlock's 0,2,0,2 margin = 4 px total. <see cref="LineSpacingPx"/>
    /// is INTENTIONALLY absent — it lives on <see cref="TileMargin"/> so it can overlap rows
    /// without changing what's inside the tile.</summary>
    public double TileWidth => IconSizePx + 8 + 2 * TilePaddingPx;
    public double TileHeight => IconSizePx + 4 + TilePaddingPx + 4 /* text margin */ + LabelAreaHeight;

    /// <summary>Margin applied to the hosting <c>ListBoxItem</c> via the ItemContainerStyle
    /// setter. Negative bottom value = CSS-negative-margin effect (next row of tiles climbs up
    /// over this tile's label without clipping its glyphs). Positive bottom = extra gap.</summary>
    public System.Windows.Thickness TileMargin => new(0, 0, 0, LineSpacingPx);

    /// <summary>Vertical breathing room around the Image, bound to <c>Image.Margin</c>. The
    /// 2-px floor keeps the icon from touching the tile's top edge even when the user dials
    /// padding all the way to 0.</summary>
    public System.Windows.Thickness IconMargin => new(0, 2 + TilePaddingPx, 0, 2);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayNameWrappable))]
    private string _displayName = string.Empty;

    /// <summary><see cref="DisplayName"/> con zero-width space (U+200B) iniettati ai SOLI
    /// confini sensati, così WPF <c>TextWrapping="Wrap"</c> preferisca andare a capo fra parole
    /// vere quando possibile e ripieghi su rotture interne solo per run davvero impossibili.
    /// Strategia:
    /// <list type="bullet">
    ///   <item>Whitespace e separatori naturali ('.', '_', '-', '/', '\') sono già break
    ///         opportunities Unicode: si lasciano intatti.</item>
    ///   <item>Confine CamelCase (lower→upper, e.g. "FileName" → "File·Name") riceve uno ZWSP.</item>
    ///   <item>Transizione lettera↔cifra (e.g. "Gen1Pokemon" → "Gen·1·Pokemon") riceve uno
    ///         ZWSP — i numeri di versione/anno spesso fanno da appiglio visivo.</item>
    ///   <item>Fallback anti-overflow: se sono passati ≥ 8 caratteri senza alcun break, si
    ///         forza uno ZWSP. Evita che un blob ininterrotto tipo "veryveryverylongword"
    ///         resti su una riga sola con ellipsis.</item>
    /// </list>
    /// <see cref="DisplayName"/> resta intatto per ricerca / rename / clipboard.</summary>
    public string DisplayNameWrappable
    {
        get
        {
            var s = DisplayName;
            if (string.IsNullOrEmpty(s) || s.Length < 2) return s;
            const int FallbackRunLimit = 8;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            var runWithoutBreak = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                sb.Append(c);

                if (char.IsWhiteSpace(c) || c == '.' || c == '_' || c == '-' || c == '/' || c == '\\')
                {
                    runWithoutBreak = 0;
                    continue;
                }
                runWithoutBreak++;
                if (i + 1 >= s.Length) continue;

                var next = s[i + 1];
                var camelBoundary = char.IsLower(c) && char.IsUpper(next);
                var letterDigitBoundary =
                    (char.IsLetter(c) && char.IsDigit(next)) ||
                    (char.IsDigit(c) && char.IsLetter(next));
                var fallback = runWithoutBreak >= FallbackRunLimit;

                if (camelBoundary || letterDigitBoundary || fallback)
                {
                    sb.Append('​');
                    runWithoutBreak = 0;
                }
            }
            return sb.ToString();
        }
    }

    [ObservableProperty]
    private BitmapSource? _icon;

    /// <summary>True while this item is in the "cut" state — the user pressed Ctrl+X in the
    /// hosting wormhole and the clipboard still carries the corresponding CF_HDROP. Bound to
    /// the tile's Opacity via the BoolToOpacityConverter so cut items render at 50 % opacity,
    /// matching Explorer's behaviour. Reset when the user pastes elsewhere or when something
    /// else takes over the clipboard (detected by the hosting window's WM_CLIPBOARDUPDATE
    /// hook). Does not persist across restarts — purely an in-memory UI hint.</summary>
    [ObservableProperty]
    private bool _isCutMarked;

    public WormholeItemViewModel(
        string absolutePath,
        IconService icons,
        int iconSizePx,
        int tilePaddingPx,
        int lineSpacingPx = 0,
        int labelFontSizePx = 11,
        int labelMaxLines = 2)
    {
        AbsolutePath = absolutePath;
        IconSizePx = iconSizePx;
        TilePaddingPx = tilePaddingPx;
        LineSpacingPx = lineSpacingPx;
        LabelFontSizePx = labelFontSizePx;
        LabelMaxLines = labelMaxLines;
        DisplayName = ResolveDisplayName(absolutePath);
        Icon = icons.GetIconAtSize(absolutePath, iconSizePx);
    }

    /// <summary>Strip the <c>.lnk</c> / <c>.url</c> extension from the visible label (Explorer
    /// does the same — keeps "Notepad" readable instead of "Notepad.lnk"). Folders keep their
    /// full name.</summary>
    private static string ResolveDisplayName(string absolutePath)
    {
        var ext = Path.GetExtension(absolutePath);
        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase))
        {
            var noExt = Path.GetFileNameWithoutExtension(absolutePath);
            return string.IsNullOrWhiteSpace(noExt) ? Path.GetFileName(absolutePath) : noExt;
        }
        return Path.GetFileName(absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
