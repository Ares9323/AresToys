using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AresToys.AI;

/// <summary>Traces a raster image to SVG with the bundled <c>vectorcraft_ffi.dll</c>
/// (vectorcraft-trace, Rust) through a small C ABI. The image is decoded, pre-processed
/// in C# (pre-blur, ignore colour, BW polarity) and handed over as straight RGBA; the
/// native side does the quantisation, tracing and SVG writing.</summary>
public sealed unsafe partial class VectorCraftImageTracer : IImageTracer
{
    /// <summary>Sentinel for "no explicit threshold; use the auto-polarity classifier".</summary>
    private const int AutoThreshold = -1;

    private const nuint MaxAnchors = 400000;

    private readonly ILogger<VectorCraftImageTracer> _logger;

    public VectorCraftImageTracer(ILogger<VectorCraftImageTracer> logger)
    {
        _logger = logger;
    }

    /// <summary>Legacy call site: maps <paramref name="colorCount"/> onto a default-
    /// constructed <see cref="TraceOptions"/> (at most 2 gives a BW silhouette with auto
    /// threshold, more gives a Color trace with that count) and delegates to the
    /// full-options overload.</summary>
    public Task<string?> TraceAsync(byte[] inputPng, int colorCount, CancellationToken cancellationToken)
    {
        var n = Math.Clamp(colorCount, 2, 16);
        var options = n <= 2
            ? new TraceOptions(Mode: TraceMode.BlackAndWhite, ColorCount: 2, Threshold: AutoThreshold)
            : new TraceOptions(Mode: TraceMode.Color, ColorCount: n);
        return TraceAsync(inputPng, options, cancellationToken);
    }

    public Task<string?> TraceAsync(byte[] inputPng, TraceOptions options, CancellationToken cancellationToken)
        => TraceDetailedAsync(inputPng, options, cancellationToken)
            .ContinueWith(t => t.GetAwaiter().GetResult().Svg, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public Task<TraceOutcome> TraceDetailedAsync(byte[] inputPng, TraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputPng);
        ArgumentNullException.ThrowIfNull(options);
        if (inputPng.Length == 0) return Task.FromResult(new TraceOutcome(null, TraceFailure.Failed));
        return Task.Run(() => Trace(inputPng, options, cancellationToken), cancellationToken);
    }

    private TraceOutcome Trace(byte[] inputPng, TraceOptions opts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!TryDecodeRgba(inputPng, out var rgba, out var width, out var height))
        {
            _logger.LogWarning("VectorCraftImageTracer: could not decode the input image");
            return new TraceOutcome(null, TraceFailure.Failed);
        }

        ApplyPreBlur(rgba, width, height, opts.PreBlurStrength);
        ApplyIgnoreColor(rgba, opts);

        var bw = opts.Mode == TraceMode.BlackAndWhite;
        var threshold = opts.Threshold;
        var avgFg = default(SKColor);
        if (bw)
        {
            var hasIgnore = opts.IgnoreColor is not null;
            var invert = false;
            if (!hasIgnore && threshold < 0)
            {
                invert = !DarkIsForeground(rgba);
                threshold = 128;
            }
            else if (threshold < 0)
            {
                threshold = 128;
            }
            avgFg = ComputeForegroundAverage(rgba, hasIgnore, invert, threshold);
            if (hasIgnore)
            {
                // IgnoreColor in BW mode means "drop this colour, trace what is left"
                // whatever its luma, so every remaining opaque pixel becomes ink.
                ForceInk(rgba);
            }
            else if (invert)
            {
                InvertRgb(rgba);
            }
        }
        else if (threshold < 0)
        {
            threshold = 128;
        }
        threshold = Math.Clamp(threshold, 0, 255);

        byte[]? swatches = null;
        var swatchCount = 0;
        var paramsJson = BuildParams(opts, threshold, out var customPalette);
        if (customPalette is { Count: > 0 })
        {
            swatchCount = customPalette.Count;
            swatches = new byte[swatchCount * 3];
            for (var i = 0; i < swatchCount; i++)
            {
                swatches[i * 3] = customPalette[i].R;
                swatches[i * 3 + 1] = customPalette[i].G;
                swatches[i * 3 + 2] = customPalette[i].B;
            }
        }

        var svg = CallNative(rgba, (uint)width, (uint)height, paramsJson, swatches, swatchCount,
            opts.AutoGrouping, ct, out var failure);
        if (svg is null) return new TraceOutcome(null, failure);

        if (bw && (avgFg.Red > 8 || avgFg.Green > 8 || avgFg.Blue > 8))
        {
            var hex = string.Create(CultureInfo.InvariantCulture,
                $"#{avgFg.Red:X2}{avgFg.Green:X2}{avgFg.Blue:X2}");
            svg = svg.Replace("fill=\"#000000\"", $"fill=\"{hex}\"", StringComparison.Ordinal);
        }
        return new TraceOutcome(svg, TraceFailure.None);
    }

    private string? CallNative(byte[] rgba, uint width, uint height, byte[] paramsJson, byte[]? swatches,
        int swatchCount, bool groupLayers, CancellationToken ct, out TraceFailure failure)
    {
        failure = TraceFailure.Failed;
        var holder = new CancelHolder(ct);
        var handle = GCHandle.Alloc(holder);
        byte* outSvg = null;
        nuint outLen = 0;
        int status;
        try
        {
            fixed (byte* pRgba = rgba)
            fixed (byte* pParams = paramsJson)
            fixed (byte* pSwatches = swatches)
            {
                status = vc_trace_svg(pRgba, width, height, pParams, (nuint)paramsJson.Length,
                    pSwatches, (nuint)swatchCount, MaxAnchors, groupLayers ? 1 : 0,
                    &ProgressCallback, (void*)GCHandle.ToIntPtr(handle), &outSvg, &outLen);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "VectorCraftImageTracer: vectorcraft_ffi.dll is missing or incompatible");
            return null;
        }
        finally
        {
            handle.Free();
        }

        try
        {
            if (status == 1) throw new OperationCanceledException(ct);
            if (status != 0)
            {
                if (status == 8) failure = TraceFailure.NotFlat;
                _logger.LogWarning("VectorCraftImageTracer: trace failed with status {Status} ({Name})",
                    status, StatusName(status));
                return null;
            }
            if (outSvg == null || outLen == 0)
            {
                _logger.LogWarning("VectorCraftImageTracer: the tracer returned an empty SVG");
                return null;
            }
            failure = TraceFailure.None;
            return Encoding.UTF8.GetString(outSvg, checked((int)outLen));
        }
        finally
        {
            if (outSvg != null) vc_free(outSvg, outLen);
        }
    }

    private static string StatusName(int status) => status switch
    {
        2 => "bad params",
        3 => "bad raster",
        4 => "bad palette",
        5 => "too complex",
        6 => "no paths",
        7 => "panic",
        8 => "not flat",
        _ => "unknown",
    };

    private sealed class CancelHolder(CancellationToken token)
    {
        public readonly CancellationToken Token = token;
    }

    /// <summary>Progress hook called by the native tracer. Returns 0 to ask it to stop.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ProgressCallback(void* ctx, ulong done, ulong total)
    {
        var holder = (CancelHolder?)GCHandle.FromIntPtr((IntPtr)ctx).Target;
        return holder is not null && holder.Token.IsCancellationRequested ? 0 : 1;
    }

    [LibraryImport("vectorcraft_ffi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int vc_trace_svg(
        byte* rgba, uint width, uint height, byte* paramsJson, nuint paramsLen,
        byte* swatchesRgb, nuint swatchCount, nuint maxAnchors, int groupLayers,
        delegate* unmanaged[Cdecl]<void*, ulong, ulong, int> progress, void* ctx,
        byte** outSvg, nuint* outLen);

    [LibraryImport("vectorcraft_ffi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void vc_free(byte* ptr, nuint len);

    private static byte[] BuildParams(TraceOptions o, int threshold,
        out IReadOnlyList<System.Drawing.Color>? customPalette)
    {
        customPalette = null;
        var palette = o.Palette switch
        {
            TracePalette.Automatic => "automatic",
            TracePalette.FullTone => "fullTone",
            TracePalette.Custom when o.CustomPalette is { Count: > 0 } => "documentLibrary",
            _ => "limited",
        };
        if (palette == "documentLibrary") customPalette = o.CustomPalette;

        var mode = o.Mode switch
        {
            TraceMode.BlackAndWhite => "blackAndWhite",
            TraceMode.Grayscale => "grayscale",
            TraceMode.Logo => "logo",
            _ => "color",
        };
        var colors = o.Mode switch
        {
            TraceMode.Grayscale => Math.Clamp(o.ColorCount, 2, 256),
            TraceMode.Logo => Math.Clamp(o.ColorCount, 2, 10),
            _ => Math.Clamp(o.ColorCount, 2, 64),
        };

        var payload = new Dictionary<string, object>
        {
            ["mode"] = mode,
            ["threshold"] = threshold,
            ["palette"] = palette,
            ["colors"] = colors,
            ["colorDetail"] = o.ColorDetail,
            ["paths"] = o.PathsPercent,
            ["corners"] = o.CornersPercent,
            ["noise"] = o.NoisePx,
            ["method"] = o.Method == TraceMethod.Abutting ? "abutting" : "overlapping",
            ["ignoreWhite"] = o.Mode == TraceMode.BlackAndWhite,
            ["snapCurvesToLines"] = o.SnapCurvesToLines,
            ["shapes"] = o.Shapes,
            ["gradients"] = o.Gradients,
            ["fills"] = o.Fills,
            ["strokes"] = o.Strokes,
            ["strokeWidth"] = o.StrokeWidth,
            ["autoGrouping"] = false,
        };
        if (o.Mode == TraceMode.Logo && o.CustomPalette is { Count: > 0 })
            payload["logoColors"] = o.CustomPalette.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}").ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static bool TryDecodeRgba(byte[] png, out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = height = 0;
        using var bmp = SKBitmap.Decode(png);
        if (bmp is null || bmp.Width <= 0 || bmp.Height <= 0) return false;
        width = bmp.Width;
        height = bmp.Height;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        rgba = new byte[checked(width * height * 4)];
        using var image = SKImage.FromBitmap(bmp);
        fixed (byte* p = rgba)
        {
            return image.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0);
        }
    }

    /// <summary>0-3 passes of a 3x3 box blur over RGBA (border pixels average over the
    /// neighbours that exist; alpha is averaged alongside RGB).</summary>
    private static void ApplyPreBlur(byte[] rgba, int w, int h, int passes)
    {
        passes = Math.Clamp(passes, 0, 3);
        if (passes == 0) return;
        var src = rgba;
        var dst = new byte[rgba.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            for (var y = 0; y < h; y++)
            {
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(h - 1, y + 1);
                for (var x = 0; x < w; x++)
                {
                    var x0 = Math.Max(0, x - 1);
                    var x1 = Math.Min(w - 1, x + 1);
                    int r = 0, g = 0, b = 0, a = 0, count = 0;
                    for (var ny = y0; ny <= y1; ny++)
                    {
                        for (var nx = x0; nx <= x1; nx++)
                        {
                            var i = (ny * w + nx) * 4;
                            r += src[i]; g += src[i + 1]; b += src[i + 2]; a += src[i + 3];
                            count++;
                        }
                    }
                    var o = (y * w + x) * 4;
                    dst[o] = (byte)(r / count);
                    dst[o + 1] = (byte)(g / count);
                    dst[o + 2] = (byte)(b / count);
                    dst[o + 3] = (byte)(a / count);
                }
            }
            (src, dst) = (dst, src);
        }
        if (!ReferenceEquals(src, rgba)) Buffer.BlockCopy(src, 0, rgba, 0, rgba.Length);
    }

    /// <summary>Pixels within <see cref="TraceOptions.IgnoreTolerance"/> per channel of
    /// <see cref="TraceOptions.IgnoreColor"/> become transparent, in any mode.</summary>
    private static void ApplyIgnoreColor(byte[] rgba, TraceOptions o)
    {
        if (o.IgnoreColor is not { } ic) return;
        var tol = o.IgnoreTolerance;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (Math.Abs(rgba[i] - ic.R) <= tol
                && Math.Abs(rgba[i + 1] - ic.G) <= tol
                && Math.Abs(rgba[i + 2] - ic.B) <= tol)
            {
                rgba[i + 3] = 0;
            }
        }
    }

    /// <summary>Minority luma cluster is the foreground: dark when dark pixels are not the
    /// majority (a tie counts as dark). Samples about 10K pixels; transparent pixels
    /// (alpha under 16) count for neither side.</summary>
    private static bool DarkIsForeground(byte[] rgba)
    {
        var pixels = rgba.Length / 4;
        var stride = Math.Max(1, pixels / 10000);
        long dark = 0, light = 0;
        for (var p = 0; p < pixels; p += stride)
        {
            var i = p * 4;
            if (rgba[i + 3] < 16) continue;
            var luma = 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];
            if (luma < 128) dark++; else light++;
        }
        return dark <= light;
    }

    /// <summary>Average colour of the pixels that will be traced as ink, measured on the
    /// image before any inversion. Black when there are none.</summary>
    private static SKColor ComputeForegroundAverage(byte[] rgba, bool hasIgnore, bool inverted, int threshold)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 16) continue;
            bool fg;
            if (hasIgnore) fg = true;
            else
            {
                var luma = 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];
                fg = inverted ? luma >= threshold : luma < threshold;
            }
            if (!fg) continue;
            r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
        }
        return n > 0 ? new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n), 255) : SKColors.Black;
    }

    private static void InvertRgb(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(255 - rgba[i]);
            rgba[i + 1] = (byte)(255 - rgba[i + 1]);
            rgba[i + 2] = (byte)(255 - rgba[i + 2]);
        }
    }

    private static void ForceInk(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 16) continue;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 0;
        }
    }
}
