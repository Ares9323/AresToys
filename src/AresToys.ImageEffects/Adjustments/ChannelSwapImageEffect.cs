using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

public enum ChannelSwapMode { RedGreen, RedBlue, GreenBlue, RotateRGB, RotateBGR }

/// <summary>Effect inspired by ShareX.</summary>
public sealed class ChannelSwapImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "channel_swap";
    public override string Name => "Channel swap";

    public ChannelSwapMode Mode { get; set; } = ChannelSwapMode.RedGreen;

    public override SKBitmap Apply(SKBitmap source) =>
        ApplyPixelOperation(source, c => Mode switch
        {
            ChannelSwapMode.RedGreen => new SKColor(c.Green, c.Red, c.Blue, c.Alpha),
            ChannelSwapMode.RedBlue => new SKColor(c.Blue, c.Green, c.Red, c.Alpha),
            ChannelSwapMode.GreenBlue => new SKColor(c.Red, c.Blue, c.Green, c.Alpha),
            ChannelSwapMode.RotateRGB => new SKColor(c.Blue, c.Red, c.Green, c.Alpha),
            ChannelSwapMode.RotateBGR => new SKColor(c.Green, c.Blue, c.Red, c.Alpha),
            _ => c,
        });
}
