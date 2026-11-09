namespace AresToys.AI;

/// <summary>Strips the background from an image, returning a new PNG with the subject
/// isolated on a transparent canvas. Implementations are free to use whatever
/// segmentation / matting model they want; the input/output contract is just bytes ↔ bytes
/// so callers (pipeline tasks, editor buttons) don't depend on Skia / ONNX types.
///
/// Async because inference can take 100ms-3s depending on hardware and model. Cancellation
/// lets a slow run be cut short when e.g. the user closes the editor mid-process.</summary>
public interface IBackgroundRemover
{
    /// <summary>Process an input PNG and return a PNG with the background pixels' alpha
    /// set to zero. Subject pixels keep their original RGB. Throws on decode failure;
    /// returns the input bytes unchanged when the model can't be loaded (caller-friendly
    /// degradation — pipeline keeps moving).</summary>
    Task<byte[]> RemoveBackgroundAsync(byte[] inputPng, CancellationToken cancellationToken);

    /// <summary>Run the segmentation model and return ONLY the saliency mask as a grayscale
    /// PNG (R=G=B = mask value, alpha=255). Returned image has the same dimensions as the
    /// input. Used by the BgRemoverWindow to drive interactive post-processing locally
    /// (threshold / feather / edge offset / brush) without re-running ONNX on every slider
    /// change. Returns <c>null</c> when the model can't be loaded so callers can fall back
    /// to a non-AI flow without inspecting bytes.</summary>
    Task<byte[]?> ExtractAlphaMaskAsync(byte[] inputPng, CancellationToken cancellationToken);
}
