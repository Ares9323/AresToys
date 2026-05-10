using System.Text.Json;
using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using AresToys.Storage.Settings;

namespace AresToys.Editor.Persistence;

public sealed record EditorDefaults(
    ShapeColor Outline,
    ShapeColor Fill,
    double StrokeWidth,
    EditorTool Tool,
    TextStyle TextStyle,
    bool FreehandSmooth = true,
    bool FreehandStartCap = false,
    bool FreehandEndCap = false,
    // Line/Arrow share the same internal tool but persist distinct cap defaults so the two
    // toolbar buttons keep their identity ("clicking Arrow gives me an arrow"). LineTipStyle
    // is shared across line/arrow/freehand — picking a tip style is a global aesthetic choice.
    bool LineStartCap = false,
    bool LineEndCap = false,
    bool ArrowStartCap = false,
    bool ArrowEndCap = true,
    LineTipStyle LineTipStyle = LineTipStyle.ShareXCurve);

public sealed class EditorDefaultsStore
{
    private const string SettingsKey = "editor.defaults";

    public static readonly EditorDefaults Initial =
        new(ShapeColor.Red, ShapeColor.Transparent, 2, EditorTool.Rectangle, TextStyle.Default,
            FreehandSmooth: true);

    private readonly ISettingsStore _settings;

    public EditorDefaultsStore(ISettingsStore settings)
    {
        _settings = settings;
    }

    public async Task<EditorDefaults> LoadAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return Initial;

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(raw);
            if (dto is null) return Initial;
            var family = string.IsNullOrWhiteSpace(dto.FontFamily) ? TextStyle.Default.FontFamily : dto.FontFamily;
            var size = dto.FontSize > 0 ? dto.FontSize : TextStyle.Default.FontSize;
            // TextColorA == 0 in pre-existing payloads (predates this field) means "missing" — fall back to Default's color.
            var textColor = dto.TextColorA == 0 && dto.TextColorR == 0 && dto.TextColorG == 0 && dto.TextColorB == 0
                ? TextStyle.Default.Color
                : new ShapeColor(dto.TextColorA, dto.TextColorR, dto.TextColorG, dto.TextColorB);
            var align = Enum.IsDefined(typeof(TextAlign), dto.Align) ? (TextAlign)dto.Align : TextAlign.Left;
            // Legacy migration: the pre-0.1.5 payload only had `FreehandEndArrow` (bool); map it
            // into the new EndCap. StartCap had no equivalent so it stays at its default (false).
            var freehandEndCap = dto.FreehandEndCap || dto.FreehandEndArrow;
            var tipStyle = Enum.IsDefined(typeof(LineTipStyle), dto.LineTipStyle)
                ? (LineTipStyle)dto.LineTipStyle
                : LineTipStyle.ShareXCurve;
            return new EditorDefaults(
                new ShapeColor(dto.OutlineA, dto.OutlineR, dto.OutlineG, dto.OutlineB),
                new ShapeColor(dto.FillA, dto.FillR, dto.FillG, dto.FillB),
                dto.StrokeWidth,
                Enum.IsDefined(typeof(EditorTool), dto.Tool) ? (EditorTool)dto.Tool : Initial.Tool,
                new TextStyle(family, size, dto.Bold, dto.Italic, textColor, align),
                FreehandSmooth: dto.FreehandSmooth,
                FreehandStartCap: dto.FreehandStartCap,
                FreehandEndCap: freehandEndCap,
                LineStartCap: dto.LineStartCap,
                LineEndCap: dto.LineEndCap,
                ArrowStartCap: dto.ArrowStartCap,
                ArrowEndCap: dto.ArrowEndCap,
                LineTipStyle: tipStyle);
        }
        catch (JsonException)
        {
            return Initial;
        }
    }

    public async Task SaveAsync(EditorDefaults defaults, CancellationToken cancellationToken)
    {
        // FreehandEndArrow is always written as `false` — once a user saves through the new code
        // the legacy field is harmlessly cleared, future loads ignore it.
        var dto = new Dto(
            defaults.Outline.A, defaults.Outline.R, defaults.Outline.G, defaults.Outline.B,
            defaults.Fill.A, defaults.Fill.R, defaults.Fill.G, defaults.Fill.B,
            defaults.StrokeWidth,
            (int)defaults.Tool,
            defaults.TextStyle.FontFamily,
            defaults.TextStyle.FontSize,
            defaults.TextStyle.Bold,
            defaults.TextStyle.Italic,
            defaults.TextStyle.Color.A, defaults.TextStyle.Color.R, defaults.TextStyle.Color.G, defaults.TextStyle.Color.B,
            (int)defaults.TextStyle.Align,
            defaults.FreehandSmooth,
            FreehandEndArrow: false,
            defaults.FreehandStartCap,
            defaults.FreehandEndCap,
            defaults.LineStartCap,
            defaults.LineEndCap,
            defaults.ArrowStartCap,
            defaults.ArrowEndCap,
            (int)defaults.LineTipStyle);
        var json = JsonSerializer.Serialize(dto);
        await _settings.SetAsync(SettingsKey, json, sensitive: false, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Dto(
        byte OutlineA, byte OutlineR, byte OutlineG, byte OutlineB,
        byte FillA, byte FillR, byte FillG, byte FillB,
        double StrokeWidth,
        int Tool,
        string FontFamily,
        double FontSize,
        bool Bold,
        bool Italic,
        byte TextColorA = 0, byte TextColorR = 0, byte TextColorG = 0, byte TextColorB = 0,
        int Align = 0,
        // Defaults to true so older payloads (pre-Smooth field) load with smoothing enabled —
        // matches the new "smooth on by default" UX.
        bool FreehandSmooth = true,
        // Legacy field (pre-0.1.5). Kept on the Dto only for load-time migration into the new
        // FreehandEndCap; SaveAsync always writes it as `false`.
        bool FreehandEndArrow = false,
        // New cap fields (pre-existing payloads load with all-false defaults except ArrowEndCap,
        // which mirrors the historical "arrow has the head" behaviour).
        bool FreehandStartCap = false,
        bool FreehandEndCap = false,
        bool LineStartCap = false,
        bool LineEndCap = false,
        bool ArrowStartCap = false,
        bool ArrowEndCap = true,
        int LineTipStyle = 0);
}
