using System.Text.Json;
using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using AresToys.Editor.ViewModels;
using AresToys.Storage.Settings;

namespace AresToys.Editor.Persistence;

/// <summary>Loads and saves the editor's <see cref="ToolStyleBook"/> (each tool's current style
/// and Recent list) plus the tool the editor was left on, under the settings key
/// <c>editor.styles</c>. Loading is tolerant: bad JSON, unknown tool names and missing fields fall
/// back to <see cref="ToolStyle.InitialFor"/>. When the key is empty the old
/// <c>editor.defaults</c> payload is migrated into the book once.</summary>
public sealed class ToolStyleStore
{
    private const string SettingsKey = "editor.styles";

    private readonly ISettingsStore _settings;

    public ToolStyleStore(ISettingsStore settings)
    {
        _settings = settings;
    }

    public async Task<(ToolStyleBook Book, EditorTool Tool)> LoadAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw))
            return await MigrateFromDefaultsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var dto = JsonSerializer.Deserialize<BookDto>(raw);
            if (dto is null) return (new ToolStyleBook(), EditorTool.Rectangle);

            var book = new ToolStyleBook();
            foreach (var entry in dto.Tools ?? [])
            {
                if (entry is null || !Enum.TryParse<EditorTool>(entry.Tool, ignoreCase: false, out var tool)
                    || !ToolStyleBook.IsStyled(tool)) continue;
                if (entry.Current is not null) book.SetCurrent(tool, FromDto(tool, entry.Current));
                if (entry.Recent is not null)
                    book.LoadRecent(tool, entry.Recent.OfType<StyleDto>().Select(r => FromDto(tool, r)));
            }

            var lastTool = Enum.TryParse<EditorTool>(dto.LastTool, ignoreCase: false, out var t) && Enum.IsDefined(t)
                ? t
                : EditorTool.Rectangle;
            return (book, lastTool);
        }
        catch (JsonException)
        {
            return (new ToolStyleBook(), EditorTool.Rectangle);
        }
    }

    public async Task SaveAsync(ToolStyleBook book, EditorTool tool, CancellationToken cancellationToken)
    {
        var dto = new BookDto
        {
            LastTool = tool.ToString(),
            Tools = ToolStyleBook.Tools
                .Select(t => new ToolDto
                {
                    Tool = t.ToString(),
                    Current = ToDto(book.Current(t)),
                    Recent = book.Recent(t).Select(ToDto).ToList()
                })
                .ToList()
        };
        var json = JsonSerializer.Serialize(dto);
        await _settings.SetAsync(SettingsKey, json, sensitive: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>First run after the update: build the book out of the old single set of defaults.
    /// The shared look goes into every shape tool; caps, smoothing, text and effect values go to
    /// the tool that owned them. Recent starts empty.</summary>
    private async Task<(ToolStyleBook Book, EditorTool Tool)> MigrateFromDefaultsAsync(CancellationToken cancellationToken)
    {
        var d = await new EditorDefaultsStore(_settings).LoadAsync(cancellationToken).ConfigureAwait(false);
        var book = new ToolStyleBook();
        foreach (var tool in ToolStyleBook.Tools)
        {
            var s = ToolStyle.InitialFor(tool) with
            {
                Outline = d.Outline,
                Fill = d.Fill,
                StrokeWidth = d.StrokeWidth,
                TipStyle = d.LineTipStyle
            };
            s = tool switch
            {
                EditorTool.Line => s with { StartCap = d.LineStartCap, EndCap = d.LineEndCap },
                EditorTool.Arrow => s with { StartCap = d.ArrowStartCap, EndCap = d.ArrowEndCap },
                EditorTool.Freehand => s with { StartCap = d.FreehandStartCap, EndCap = d.FreehandEndCap, Smooth = d.FreehandSmooth },
                EditorTool.Text => s with
                {
                    Text = d.TextStyle,
                    TextOutlineColor = d.TextOutlineColor ?? ShapeColor.Black,
                    TextOutlineWidth = d.TextOutlineWidth
                },
                EditorTool.Blur => s with { EffectAmount = d.BlurRadius },
                EditorTool.Pixelate => s with { EffectAmount = d.PixelateBlockSize },
                EditorTool.Spotlight => s with { EffectAmount = d.SpotlightDim, EffectAmount2 = d.SpotlightBlur },
                _ => s
            };
            book.SetCurrent(tool, s);
        }
        return (book, d.Tool);
    }

    private static ToolStyle FromDto(EditorTool tool, StyleDto dto)
    {
        var i = ToolStyle.InitialFor(tool);
        var textDefault = i.Text;
        var family = string.IsNullOrWhiteSpace(dto.FontFamily) ? textDefault.FontFamily : dto.FontFamily;
        var size = dto.FontSize is > 0 ? dto.FontSize.Value : textDefault.FontSize;
        var align = dto.Align is { } a && Enum.IsDefined(typeof(TextAlign), a) ? (TextAlign)a : textDefault.Align;
        var tip = dto.TipStyle is { } ts && Enum.IsDefined(typeof(LineTipStyle), ts) ? (LineTipStyle)ts : i.TipStyle;
        // Clamped on the way in so a hand-edited or corrupted payload can't produce a blur that
        // takes seconds to render or a mosaic with a zero-size cell.
        var amount = tool switch
        {
            EditorTool.Blur => Math.Clamp(dto.EffectAmount ?? i.EffectAmount, 1, 60),
            EditorTool.Pixelate => Math.Clamp(dto.EffectAmount ?? i.EffectAmount, 2, 60),
            EditorTool.Spotlight => Math.Clamp(dto.EffectAmount ?? i.EffectAmount, 0, 1),
            _ => i.EffectAmount
        };
        var amount2 = tool == EditorTool.Spotlight ? Math.Clamp(dto.EffectAmount2 ?? i.EffectAmount2, 0, 60) : i.EffectAmount2;
        return new ToolStyle(
            ColorFrom(dto.Outline, i.Outline),
            ColorFrom(dto.Fill, i.Fill),
            dto.StrokeWidth ?? i.StrokeWidth,
            dto.StartCap ?? i.StartCap,
            dto.EndCap ?? i.EndCap,
            tip,
            dto.Smooth ?? i.Smooth,
            new TextStyle(family, size, dto.Bold ?? textDefault.Bold, dto.Italic ?? textDefault.Italic,
                ColorFrom(dto.TextColor, textDefault.Color), align),
            ColorFrom(dto.TextOutline, i.TextOutlineColor),
            dto.TextOutlineWidth ?? i.TextOutlineWidth,
            amount,
            amount2,
            string.IsNullOrWhiteSpace(dto.StepFontFamily) ? i.StepFontFamily : dto.StepFontFamily,
            dto.StepBold ?? i.StepBold,
            dto.StepItalic ?? i.StepItalic);
    }

    private static StyleDto ToDto(ToolStyle s) => new()
    {
        Outline = ColorTo(s.Outline),
        Fill = ColorTo(s.Fill),
        StrokeWidth = s.StrokeWidth,
        StartCap = s.StartCap,
        EndCap = s.EndCap,
        TipStyle = (int)s.TipStyle,
        Smooth = s.Smooth,
        FontFamily = s.Text.FontFamily,
        FontSize = s.Text.FontSize,
        Bold = s.Text.Bold,
        Italic = s.Text.Italic,
        TextColor = ColorTo(s.Text.Color),
        Align = (int)s.Text.Align,
        TextOutline = ColorTo(s.TextOutlineColor),
        TextOutlineWidth = s.TextOutlineWidth,
        EffectAmount = s.EffectAmount,
        EffectAmount2 = s.EffectAmount2,
        StepFontFamily = s.StepFontFamily,
        StepBold = s.StepBold,
        StepItalic = s.StepItalic
    };

    private static ShapeColor ColorFrom(ColorDto? dto, ShapeColor fallback) =>
        dto is null ? fallback : new ShapeColor(dto.A, dto.R, dto.G, dto.B);

    private static ColorDto ColorTo(ShapeColor c) => new() { A = c.A, R = c.R, G = c.G, B = c.B };

    private sealed class ColorDto
    {
        public byte A { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }

    // Every field is nullable so a payload missing one (older build, hand edit) falls back to the
    // tool's initial value instead of to a zero.
    private sealed class StyleDto
    {
        public ColorDto? Outline { get; set; }
        public ColorDto? Fill { get; set; }
        public double? StrokeWidth { get; set; }
        public bool? StartCap { get; set; }
        public bool? EndCap { get; set; }
        public int? TipStyle { get; set; }
        public bool? Smooth { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSize { get; set; }
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }
        public ColorDto? TextColor { get; set; }
        public int? Align { get; set; }
        public ColorDto? TextOutline { get; set; }
        public double? TextOutlineWidth { get; set; }
        public double? EffectAmount { get; set; }
        public double? EffectAmount2 { get; set; }
        public string? StepFontFamily { get; set; }
        public bool? StepBold { get; set; }
        public bool? StepItalic { get; set; }
    }

    private sealed class ToolDto
    {
        public string? Tool { get; set; }
        public StyleDto? Current { get; set; }
        public List<StyleDto>? Recent { get; set; }
    }

    private sealed class BookDto
    {
        public string? LastTool { get; set; }
        public List<ToolDto>? Tools { get; set; }
    }
}
