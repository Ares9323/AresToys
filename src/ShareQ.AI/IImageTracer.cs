namespace ShareQ.AI;

/// <summary>Convert a raster image to a vector SVG document. Implementations may use
/// different tracers (potrace, autotrace, AI-based). Returns the SVG as a string —
/// callers write it to disk / clipboard themselves so we don't bake any I/O policy in.
///
/// <see cref="TraceAsync(byte[], int, CancellationToken)"/>'s <c>colorCount</c> parameter:
/// ≤ 2 yields a monochrome trace (the cleanest output, ideal for icons / line art).
/// Larger values quantise the input to that many colours and stack per-colour traces in
/// the same SVG.</summary>
public interface IImageTracer
{
    Task<string?> TraceAsync(byte[] inputPng, int colorCount, CancellationToken cancellationToken);

    /// <summary>Full-options overload — routed by the preview window. The original
    /// <c>colorCount</c> overload should default-construct <see cref="TraceOptions"/> and
    /// call into this one so call sites (pipeline task, future API consumers) keep working.</summary>
    Task<string?> TraceAsync(byte[] inputPng, TraceOptions options, CancellationToken cancellationToken);
}
