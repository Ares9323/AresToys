using AresToys.ImageEffects.Drawing;
using SkiaSharp;

namespace AresToys.App.Services.ImageEffects;

/// <summary>Curated gradient presets for the editor's "Presets" panel. ShareX ships with
/// well over 100; the vast majority overlap or only differ in one stop position. We pick
/// ten broadly-useful ones — five photo / sky / nature looks, three brand-style two-stop
/// fades, and two utilities — and stop there. The user can still build any gradient by
/// hand; presets are purely a starting point.</summary>
public static class GradientPresets
{
    public sealed record Preset(string Name, GradientInfo Gradient);

    private static GradientStop Stop(byte r, byte g, byte b, float location, byte a = 255) =>
        new(new SKColor(r, g, b, a), location);

    public static IReadOnlyList<Preset> All { get; } = BuildPresets();

    private static List<Preset> BuildPresets() => new()
    {
        // Photographic / sky.
        new("Sunset", new GradientInfo(LinearGradientMode.Vertical,
            Stop(0xFF, 0x7E, 0x5F, 0),
            Stop(0xFE, 0xB4, 0x7B, 50),
            Stop(0x40, 0x10, 0x40, 100))),
        new("Ocean", new GradientInfo(LinearGradientMode.Vertical,
            Stop(0x12, 0xC2, 0xE9, 0),
            Stop(0x06, 0x69, 0xA7, 50),
            Stop(0x06, 0x2E, 0x5F, 100))),
        new("Forest", new GradientInfo(LinearGradientMode.Vertical,
            Stop(0x13, 0x4E, 0x5E, 0),
            Stop(0x71, 0xB2, 0x80, 100))),
        new("Sunrise", new GradientInfo(LinearGradientMode.Vertical,
            Stop(0x40, 0x2A, 0x73, 0),
            Stop(0xFD, 0x74, 0x6C, 60),
            Stop(0xFE, 0xB4, 0x7B, 100))),
        new("Rose", new GradientInfo(LinearGradientMode.ForwardDiagonal,
            Stop(0xFF, 0x95, 0xA8, 0),
            Stop(0xCB, 0x4D, 0x73, 100))),
        new("Rainbow", new GradientInfo(LinearGradientMode.Horizontal,
            Stop(0xFF, 0x00, 0x00, 0),
            Stop(0xFF, 0x7F, 0x00, 16.6f),
            Stop(0xFF, 0xFF, 0x00, 33.3f),
            Stop(0x00, 0xC8, 0x00, 50),
            Stop(0x00, 0x96, 0xFF, 66.6f),
            Stop(0x4B, 0x00, 0x82, 83.3f),
            Stop(0x94, 0x00, 0xD3, 100))),

        // Two-stop brand fades.
        new("Blue → Purple", new GradientInfo(LinearGradientMode.ForwardDiagonal,
            Stop(0x0F, 0x70, 0xFF, 0),
            Stop(0x86, 0x2E, 0xCC, 100))),
        new("Mint → Teal", new GradientInfo(LinearGradientMode.ForwardDiagonal,
            Stop(0xA8, 0xFF, 0x78, 0),
            Stop(0x07, 0x88, 0x9B, 100))),
        new("Steel", new GradientInfo(LinearGradientMode.Vertical,
            Stop(0x5A, 0x60, 0x6E, 0),
            Stop(0x29, 0x2D, 0x37, 100))),

        // Utilities.
        new("Fade to transparent", new GradientInfo(LinearGradientMode.Vertical,
            Stop(0, 0, 0, 0, 255),
            Stop(0, 0, 0, 100, 0))),
        new("Black → White", new GradientInfo(LinearGradientMode.Vertical,
            Stop(0x00, 0x00, 0x00, 0),
            Stop(0xFF, 0xFF, 0xFF, 100))),
    };
}
