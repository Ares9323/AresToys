namespace AresToys.Core.Imaging;

/// <summary>Re-encodes raw image bytes into a different format. Implemented in
/// <c>AresToys.App</c> against WPF's <see cref="System.Windows.Media.Imaging.BitmapEncoder"/>
/// family — kept behind an interface here so <c>AresToys.Pipeline</c> (and other non-WPF
/// projects) can depend on it without pulling in PresentationCore.</summary>
public interface IImageEncoder
{
    /// <summary>Decode <paramref name="sourceBytes"/> and re-encode them in
    /// <paramref name="target"/>. <paramref name="jpegQuality"/> applies only when the target
    /// is JPEG (clamped to 1..100, default 90). Returns the new bytes; throws on undecodable
    /// input. Callers can short-circuit conversion when the source is already in the desired
    /// format — the encoder itself doesn't sniff to avoid pointless decode/encode round-trips
    /// when callers already know the source format.</summary>
    byte[] Encode(byte[] sourceBytes, ImageFormat target, int jpegQuality = 90);
}
