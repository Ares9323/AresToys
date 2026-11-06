using AresToys.ImageEffects.Drawing;
using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Mode for <see cref="CanvasImageEffect.MarginMode"/>: margin values are either
/// absolute pixel counts or a percentage of the source dimensions. Names mirror ShareX's
/// <c>CanvasMarginMode</c> so <c>.sxie</c> presets round-trip.</summary>
public enum CanvasMarginMode
{
    AbsoluteSize,
    PercentageOfCanvas,
}

/// <summary>Effect inspired by ShareX. Adds
/// padding around the source image, filled with <see cref="Color"/>. Negative paddings
/// crop the image instead. <see cref="MarginMode"/> picks pixel vs percentage interpretation.
/// Used by templates like <c>BackgroundGradient.sxie</c> to leave room for a drop shadow
/// before the gradient fill underneath.</summary>
public sealed class CanvasImageEffect : ManipulationImageEffectBase
{
    public override string Id => "canvas";
    public override string Name => "Canvas";

    public Padding Margin { get; set; }
    public CanvasMarginMode MarginMode { get; set; } = CanvasMarginMode.AbsoluteSize;
    public SKColor Color { get; set; } = SKColors.Transparent;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var margin = MarginMode == CanvasMarginMode.PercentageOfCanvas
            ? new Padding(
                (int)Math.Round(Margin.Left / 100f * source.Width),
                (int)Math.Round(Margin.Top / 100f * source.Height),
                (int)Math.Round(Margin.Right / 100f * source.Width),
                (int)Math.Round(Margin.Bottom / 100f * source.Height))
            : Margin;

        var newWidth = source.Width + margin.Left + margin.Right;
        var newHeight = source.Height + margin.Top + margin.Bottom;
        if (newWidth <= 0 || newHeight <= 0) return source.Copy();

        var result = new SKBitmap(newWidth, newHeight, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(Color);
        canvas.DrawBitmap(source, margin.Left, margin.Top);
        return result;
    }
}
