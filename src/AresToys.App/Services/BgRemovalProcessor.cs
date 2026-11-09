using SkiaSharp;

namespace AresToys.App.Services;

/// <summary>Parameter snapshot for <see cref="BgRemovalProcessor"/>. Default values yield
/// the same composite the bare <c>IBackgroundRemover.RemoveBackgroundAsync</c> produces
/// (no threshold, no feather, no edge offset) so the BgRemoverWindow's "untouched sliders"
/// state matches the editor's plain Magic Eraser one-shot.</summary>
public readonly record struct BgRemovalParams(
    /// <summary>0-255. 0 = use the model's soft saliency directly. &gt;0 = binary cutoff at
    /// this value (pixels above stay subject, below become background). Higher values cut
    /// more aggressively (drops blurry edges).</summary>
    int Threshold,
    /// <summary>0-20 px. Gaussian blur σ applied to the mask after threshold. Softens the
    /// alpha boundary; 0 = sharp, ~3-5 = natural feather, &gt;10 = halo'd look.</summary>
    int FeatherPx,
    /// <summary>-20..+20 px. Negative = erode (shrink subject; removes background-tinted
    /// halos from the edge). Positive = dilate (grow subject; preserves more border detail
    /// at the cost of bleeding background).</summary>
    int EdgeOffsetPx)
{
    public static readonly BgRemovalParams Default = new(0, 0, 0);
}

/// <summary>Pure SkiaSharp post-processing for the U2NetP saliency mask: threshold → feather
/// (Gaussian) → edge offset (erode/dilate via thresholded blur) → composite alpha onto the
/// source. The pipeline is reproducible from a parameter snapshot, so the BgRemoverWindow
/// can call it on every slider change without re-running ONNX.
///
/// All operations work on grayscale-in-BGRA SKBitmaps (R=G=B = mask value). The composite
/// step writes alpha into a copy of the source RGB.</summary>
public static class BgRemovalProcessor
{
    /// <summary>Run the full pipeline: rawMask → final composite. Caller owns the inputs and
    /// the returned bitmap. Convenience wrapper that combines <see cref="ProcessMask"/> +
    /// <see cref="BuildCompositeFromProcessed"/> for one-shot calls (e.g. final Apply); the
    /// BgRemoverWindow uses the split path so it can cache the processed mask between brush
    /// strokes.</summary>
    public static SKBitmap BuildComposite(SKBitmap source, SKBitmap rawMask, SKBitmap? brushOverlay, BgRemovalParams p)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rawMask);
        using var processed = ProcessMask(rawMask, p);
        return BuildCompositeFromProcessed(source, processed, brushOverlay);
    }

    /// <summary>Fast composite path for live brush feedback. Layers two brush surfaces over
    /// the AI mask: <paramref name="brushOverlay"/> (committed strokes, persists across the
    /// session) and <paramref name="strokeBuffer"/> (the in-flight stroke; merged into the
    /// overlay on mouse-up). Both encode Add strength in R and Remove strength in G — Add
    /// and Remove are independent channels so a Remove stroke after an Add stroke at the
    /// same pixel correctly lowers alpha instead of being max'd out.
    /// <para>Pipeline per pixel: aiAlpha → apply overlay (Add then Remove) → apply stroke
    /// (Add then Remove) → background-opacity floor → write. Each Apply step is a lerp:
    /// Add of strength s pushes alpha towards 255; Remove of strength s pushes towards 0.</para>
    /// <para>Performance: byte-loop with Marshal.Copy memcpys instead of per-pixel SkiaSharp
    /// calls — 10-20× faster on 1080p+ inputs, keeps brush updates well inside a 16 ms frame
    /// budget. <paramref name="output"/> is reused across calls so the allocator isn't hit
    /// on every brush move.</para></summary>
    public static byte[] CompositeIntoBuffer(SKBitmap source, SKBitmap processedMask, SKBitmap? brushOverlay, SKBitmap? strokeBuffer, byte[] output, byte backgroundOpacity = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(processedMask);
        ArgumentNullException.ThrowIfNull(output);
        var w = source.Width;
        var h = source.Height;
        var srcStride = source.RowBytes;
        var len = srcStride * h;
        if (output.Length < len) throw new ArgumentException("output buffer too small", nameof(output));

        var srcBuf = new byte[len];
        var maskBuf = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(source.GetPixels(), srcBuf, 0, len);
        System.Runtime.InteropServices.Marshal.Copy(processedMask.GetPixels(), maskBuf, 0, len);

        var hasBrush = brushOverlay is not null && brushOverlay.RowBytes == srcStride;
        var brushBuf = hasBrush ? new byte[len] : Array.Empty<byte>();
        if (hasBrush)
            System.Runtime.InteropServices.Marshal.Copy(brushOverlay!.GetPixels(), brushBuf, 0, len);

        var hasStroke = strokeBuffer is not null && strokeBuffer.RowBytes == srcStride;
        var strokeBuf = hasStroke ? new byte[len] : Array.Empty<byte>();
        if (hasStroke)
            System.Runtime.InteropServices.Marshal.Copy(strokeBuffer!.GetPixels(), strokeBuf, 0, len);

        var bgFloor = backgroundOpacity;
        for (var i = 0; i < len; i += 4)
        {
            int alpha = maskBuf[i + 2];

            if (hasBrush)
            {
                int oR = brushBuf[i + 2]; // Add strength
                int oG = brushBuf[i + 1]; // Remove strength
                // Add: lerp(alpha, 255, oR/255). Saturates at 255 so painting Add over an
                // already-fully-subject pixel is a no-op (no visible "trail" highlighting).
                alpha = (alpha * (255 - oR) + 255 * oR + 127) / 255;
                // Remove: lerp(alpha, 0, oG/255). Saturates at 0 the symmetric way.
                alpha = (alpha * (255 - oG) + 127) / 255;
            }
            if (hasStroke)
            {
                int sR = strokeBuf[i + 2];
                int sG = strokeBuf[i + 1];
                alpha = (alpha * (255 - sR) + 255 * sR + 127) / 255;
                alpha = (alpha * (255 - sG) + 127) / 255;
            }
            if (bgFloor != 0)
            {
                alpha = bgFloor + (255 - bgFloor) * alpha / 255;
            }
            output[i + 0] = srcBuf[i + 0];
            output[i + 1] = srcBuf[i + 1];
            output[i + 2] = srcBuf[i + 2];
            output[i + 3] = (byte)alpha;
        }
        return output;
    }

    /// <summary>Run the post-processed mask + optional brush overlay against the source and
    /// return a fresh bitmap (allocates on every call — used by the one-shot Apply path).
    /// For live preview use <see cref="CompositeIntoBuffer"/> with a reused byte buffer +
    /// WriteableBitmap so we don't pay the allocator.</summary>
    public static SKBitmap BuildCompositeFromProcessed(SKBitmap source, SKBitmap processedMask, SKBitmap? brushOverlay)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(processedMask);
        // Apply brush overlay onto a copy of processedMask so the caller's cache is untouched.
        using var withBrush = processedMask.Copy();
        if (brushOverlay is not null) ApplyBrushOverlay(withBrush, brushOverlay);
        return Composite(source, withBrush);
    }

    /// <summary>Process the raw mask through threshold + feather + edge offset, returning a
    /// fresh bitmap (same dims as input). Public so the window can drive the right-pane
    /// preview without doing the composite step (e.g. for a "view mask" debug mode in the
    /// future).</summary>
    public static SKBitmap ProcessMask(SKBitmap rawMask, BgRemovalParams p)
    {
        var current = rawMask.Copy();
        if (p.Threshold > 0)
        {
            var thresholded = ApplyThreshold(current, p.Threshold);
            current.Dispose();
            current = thresholded;
        }
        if (p.EdgeOffsetPx != 0)
        {
            var offset = ApplyEdgeOffset(current, p.EdgeOffsetPx);
            current.Dispose();
            current = offset;
        }
        if (p.FeatherPx > 0)
        {
            var feathered = ApplyFeather(current, p.FeatherPx);
            current.Dispose();
            current = feathered;
        }
        return current;
    }

    /// <summary>Hard threshold: any pixel ≥ <paramref name="threshold"/> becomes 255, the rest
    /// 0. Produces a binary mask — useful when the user wants a clean cut without anti-alias
    /// halos but accepts losing fine edge feathering.</summary>
    private static SKBitmap ApplyThreshold(SKBitmap mask, int threshold)
    {
        var w = mask.Width;
        var h = mask.Height;
        var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = mask.GetPixel(x, y).Red;
                var b = v >= threshold ? (byte)255 : (byte)0;
                dst.SetPixel(x, y, new SKColor(b, b, b, 255));
            }
        }
        return dst;
    }

    /// <summary>Gaussian blur on the mask. Cheap implementation: separable 1D box blurs done
    /// 3 times approximate a Gaussian (Wells, 1986). σ controls the iteration count and
    /// kernel radius — for σ ≤ 20 a radius of <c>round(σ)</c> is visually indistinguishable
    /// from a true Gaussian at the resolutions screenshots ship at.</summary>
    private static SKBitmap ApplyFeather(SKBitmap mask, int sigma)
    {
        var w = mask.Width;
        var h = mask.Height;
        // We render via SKCanvas + SKMaskFilter so SkiaSharp handles the convolution natively
        // (much faster than rolling our own per-pixel loop on 1080p+ images). Output is a
        // fresh bitmap that the caller owns.
        var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
        };
        canvas.DrawBitmap(mask, 0, 0, paint);
        return dst;
    }

    /// <summary>Erode (negative offset) or dilate (positive offset) the mask by approximately
    /// <paramref name="offsetPx"/> pixels. Implemented as a Gaussian blur at σ = |offset|
    /// followed by a threshold at the appropriate level: blur+threshold-low = dilate,
    /// blur+threshold-high = erode. This is the cheap "morphological op via blur+threshold"
    /// trick — accurate to within a pixel for offsets ≤ 20 and avoids the cost of a true
    /// per-pixel min/max kernel.</summary>
    private static SKBitmap ApplyEdgeOffset(SKBitmap mask, int offsetPx)
    {
        var sigma = Math.Abs(offsetPx);
        if (sigma == 0) return mask.Copy();
        using var blurred = ApplyFeather(mask, sigma);
        // Negative offset (erode): only pixels that were strongly subject before stay; the
        // blurred edge falls below threshold-128 and is dropped. Positive offset (dilate):
        // pixels with even slight signal pass — the blurred edge stays above threshold-128.
        // The 64 / 192 cutoffs are chosen so 1 sigma ≈ 1 pixel of effective offset.
        var threshold = offsetPx < 0 ? 192 : 64;
        return ApplyThreshold(blurred, threshold);
    }

    /// <summary>Apply a brush overlay on top of the processed mask in-place. The overlay
    /// uses two-channel encoding: R = Add strength (0-255), G = Remove strength (0-255).
    /// Each pixel's mask alpha is pushed up by Add then down by Remove, both via lerp.
    /// Used by the slow Apply path; the live preview uses the equivalent byte-buffer logic
    /// inside <see cref="CompositeIntoBuffer"/>.</summary>
    private static void ApplyBrushOverlay(SKBitmap processedMask, SKBitmap overlay)
    {
        var w = Math.Min(processedMask.Width, overlay.Width);
        var h = Math.Min(processedMask.Height, overlay.Height);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = overlay.GetPixel(x, y);
                int oR = o.Red;
                int oG = o.Green;
                if (oR == 0 && oG == 0) continue;
                int existing = processedMask.GetPixel(x, y).Red;
                int afterAdd = (existing * (255 - oR) + 255 * oR + 127) / 255;
                int afterRemove = (afterAdd * (255 - oG) + 127) / 255;
                var v = (byte)afterRemove;
                processedMask.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }
    }

    /// <summary>Composite: source RGB stays intact, alpha = mask.R. Returns a fresh bitmap
    /// (BGRA Unpremul) ready to encode as a PNG with proper transparency.</summary>
    private static SKBitmap Composite(SKBitmap source, SKBitmap mask)
    {
        var w = source.Width;
        var h = source.Height;
        var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var s = source.GetPixel(x, y);
                var m = mask.GetPixel(x, y).Red;
                dst.SetPixel(x, y, new SKColor(s.Red, s.Green, s.Blue, m));
            }
        }
        return dst;
    }
}
