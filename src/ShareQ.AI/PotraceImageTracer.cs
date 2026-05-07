using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ShareQ.AI;

/// <summary>Shells out to the bundled <c>potrace.exe</c> (BSD, ~200KB) to do the actual
/// raster-to-vector tracing. The native binary path keeps the surface tiny and avoids
/// pulling in a second native lib chain. Multi-color tracing layers multiple
/// monochrome traces in a single SVG.</summary>
public sealed class PotraceImageTracer : IImageTracer
{
    /// <summary>Sentinel for "no explicit threshold; use auto-polarity classifier".
    /// Used by <see cref="EncodePbm"/> so the legacy <c>colorCount</c> call site keeps
    /// the auto-detect behaviour while the new options-driven call site can pass an
    /// explicit 0-255 cutoff.</summary>
    private const int AutoThreshold = -1;

    /// <summary>Hard cap for <see cref="TracePalette.FullTone"/>; without quantization a
    /// natural photo trivially exceeds thousands of unique colours and would blow up
    /// runtime. 64 keeps the multi-layer composite tractable while still giving more
    /// gradient coverage than Limited mode.</summary>
    private const int FullToneLayerCap = 64;

    private readonly ILogger<PotraceImageTracer> _logger;

    public PotraceImageTracer(ILogger<PotraceImageTracer> logger)
    {
        _logger = logger;
    }

    /// <summary>Legacy call site: maps <paramref name="colorCount"/> onto a default-
    /// constructed <see cref="TraceOptions"/> (≤ 2 → BW silhouette, &gt; 2 → Color trace
    /// with that count) and delegates to the full-options overload. Existing callers in
    /// the pipeline / EditorLauncher keep working unchanged.</summary>
    public Task<string?> TraceAsync(byte[] inputPng, int colorCount, CancellationToken cancellationToken)
    {
        var n = Math.Clamp(colorCount, 2, 16);
        // Legacy callers (pipeline task, EditorLauncher today) didn't choose a threshold —
        // they got the EncodePbm auto-polarity classifier (minority luma cluster = fg).
        // Pass Threshold=AutoThreshold so the encoder keeps that behaviour for them; the
        // new options-driven call site uses the explicit 0-255 range from the TraceWindow UI.
        var options = n <= 2
            ? new TraceOptions(Mode: TraceMode.BlackAndWhite, ColorCount: 2, Threshold: AutoThreshold)
            : new TraceOptions(Mode: TraceMode.Color, ColorCount: n);
        return TraceAsync(inputPng, options, cancellationToken);
    }

    /// <summary>Full-options trace. Routes between the monochrome and multi-colour
    /// pipelines based on <see cref="TraceOptions.Mode"/>; both paths share the same
    /// CLI arg builder + PBM encoder, with the multi path additionally honouring the
    /// palette / method / grouping / transparency knobs.</summary>
    public async Task<string?> TraceAsync(byte[] inputPng, TraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputPng);
        ArgumentNullException.ThrowIfNull(options);
        if (inputPng.Length == 0) return null;

        var potracePath = PotraceLocator.Find();
        if (potracePath is null)
        {
            _logger.LogWarning("PotraceImageTracer: potrace.exe not found in bundled Tools/");
            return null;
        }

        return options.Mode == TraceMode.BlackAndWhite
            ? await TraceMonochromeAsync(potracePath, inputPng, options, cancellationToken).ConfigureAwait(false)
            : await TraceMultiColorAsync(potracePath, inputPng, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run potrace on the input bytes treated as a 1-bit silhouette. We feed it a
    /// PBM (P4) document on stdin and capture the SVG it writes to stdout. PBM is the
    /// simplest raster format potrace reads — strict, well-documented, and avoids the
    /// header / pixel-format ambiguities that bit us when we tried SkiaSharp's BMP encoder.</summary>
    private async Task<string?> TraceMonochromeAsync(string potracePath, byte[] inputPng, TraceOptions opts, CancellationToken ct)
    {
        var pbmBytes = ConvertToPbm(inputPng, opts, out var avgFg);
        if (pbmBytes is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = potracePath,
            Arguments = BuildPotraceArgs(opts),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start potrace process");
            await process.StandardInput.BaseStream.WriteAsync(pbmBytes, ct).ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var svg = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("potrace exited with code {Code}: {Stderr}", process.ExitCode, stderr);
                return null;
            }
            if (string.IsNullOrWhiteSpace(svg))
            {
                _logger.LogWarning("potrace produced empty SVG. stderr: {Stderr}", stderr);
                return null;
            }
            // potrace hardcodes the foreground fill to "#000000". Substitute the average
            // colour of the source's fg pixels so a "white icon on dark bg" trace renders
            // as a white silhouette instead of the wrong-tone black one. Skip the
            // substitution when the avg is already near-black so we don't waste a string
            // replace pass on the common case.
            if (avgFg.Red > 8 || avgFg.Green > 8 || avgFg.Blue > 8)
            {
                var hex = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"#{avgFg.Red:X2}{avgFg.Green:X2}{avgFg.Blue:X2}");
                svg = svg.Replace("fill=\"#000000\"", $"fill=\"{hex}\"", StringComparison.Ordinal);
            }
            return svg;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PotraceImageTracer: shell-out failed");
            return null;
        }
    }

    /// <summary>Multi-color trace: quantize the source per <see cref="TraceOptions.Palette"/>,
    /// build a binary mask per colour (foreground = pixels of that colour, anything else =
    /// background), trace each mask, wrap the resulting paths in a single SVG with the
    /// right fill colour. Layers sort largest area first so smaller details paint on top of
    /// bigger backgrounds. Honours IgnoreColor (drops the matched palette entry),
    /// Transparency (renders an opaque background rect for IgnoreColor when off),
    /// AutoGrouping (labels each layer's <c>&lt;g&gt;</c> with a stable id).</summary>
    private async Task<string?> TraceMultiColorAsync(string potracePath, byte[] inputPng, TraceOptions opts, CancellationToken ct)
    {
        using var decoded = SKBitmap.Decode(inputPng);
        if (decoded is null) return null;

        // Pre-blur the source with N passes of 3x3 box filter before any palette / assignment
        // work. The dominant source of "frastagliato" boundary noise is anti-aliased edge
        // pixels that sit ambiguously between two colours: a single AA pixel might be 60%
        // black / 40% white, and nearest-colour matching flips it across the boundary based
        // on tiny RGB variations. A gentle box blur spreads each AA pixel into its
        // neighbourhood so boundary pixels end up clearly on one side or the other after
        // quantisation. Pass count is user-driven via <see cref="TraceOptions.PreBlurStrength"/>:
        // 0 = skip entirely (raw source preserved); 1+ = N iterations of the 3x3 kernel
        // (each pass roughly equivalent to σ ≈ √N/3 Gaussian — diminishing returns past 3).
        using var rawSrc = ApplyPreBlur(decoded, Math.Clamp(opts.PreBlurStrength, 0, 3));

        // Grayscale = pre-pass to collapse colours onto the luminance diagonal so the
        // colour pipeline downstream picks gray buckets only. Cheaper than maintaining
        // a parallel grayscale tracer and produces identical output for our use case.
        using var src = opts.Mode == TraceMode.Grayscale ? ToGrayscale(rawSrc) : rawSrc.Copy();
        if (src is null) return null;

        var palette = BuildPalette(src, opts);
        if (palette.Count == 0) return null;

        // Drop the IgnoreColor from the palette so we never trace it. We optionally
        // re-add it as a static background rect below if Transparency is off.
        if (opts.IgnoreColor is { } ignore)
        {
            var ic = ToSk(ignore);
            palette.RemoveAll(c =>
                Math.Abs(c.Red - ic.Red) <= opts.IgnoreTolerance
             && Math.Abs(c.Green - ic.Green) <= opts.IgnoreTolerance
             && Math.Abs(c.Blue - ic.Blue) <= opts.IgnoreTolerance);
        }
        if (palette.Count == 0) return null;

        // Build per-pixel palette assignment ONCE before tracing. Each pixel either belongs
        // to exactly one layer (its index 0..palette.Count-1) or to "no layer" (255 = drop:
        // alpha < 16, or matches IgnoreColor). For Method=Overlapping the assignment uses
        // a tolerance-based "first-match" rule so a pixel near a colour boundary can favour
        // the dominant layer. For Method=Abutting we do nearest-palette-colour assignment
        // so each pixel belongs to exactly one layer with no double-painting on the seams.
        // The previous "BuildMaskPbm per layer with global tolerance" path also wrongly
        // included transparent source pixels in any layer they happened to share an RGB
        // with — that's the "black hole on the lion's mouth" bug — so this rewrite fixes
        // both Method semantics AND the alpha-leak in one go.
        var assignment = BuildPaletteAssignment(src, palette, opts);
        // Pre-trace smoothing: collapse boundary oscillations (the anti-aliased "edge hash"
        // that makes inner contours look frastagliato in the preview) by majority vote in a
        // 3x3 neighbourhood. Without this, anti-aliased edge pixels alternate between
        // adjacent layers based on tiny RGB variations and potrace traces every zigzag — and
        // CornersPercent / PathsPercent can't fix it because those operate on the path AFTER
        // potrace has fitted to the noisy bitmap. NoisePx (potrace -t turdsize) only kills
        // isolated specks, not the continuous boundary saw-tooth, so this pass complements
        // it: turdsize for islands, majority-filter for boundary noise. Pass count is
        // user-driven via <see cref="TraceOptions.SmoothingIterations"/>; 0 disables the
        // pass entirely, 1 catches single-pixel zigzag, 2 (default) picks up 2-pixel
        // residual notches, 3 erodes ≤1 px features (use sparingly).
        var smoothPasses = Math.Clamp(opts.SmoothingIterations, 0, 3);
        for (var i = 0; i < smoothPasses; i++)
        {
            assignment = SmoothAssignment(assignment, src.Width, src.Height);
        }

        var args = BuildPotraceArgs(opts);
        // Overlapping mode dilates each layer's mask by N pixels into adjacent (non-transparent)
        // layers before tracing. Without this, potrace smooths each layer's boundary
        // independently and adjacent layers' smoothed edges both recede slightly inward —
        // leaving 1-3 px transparent gaps on every colour seam (the "buchi" Illustrator
        // doesn't show). The dilation makes the bottom layer extend a hair under the top
        // layer; the smaller-area-on-top z-sort means the overlap is hidden visually but
        // the gap is filled. Abutting mode keeps the strict partition (closer to a "honest"
        // representation; gaps may show but layers don't double-paint on the seams).
        // Radius is user-driven via <see cref="TraceOptions.OverlapRadius"/>; 0 = no
        // dilation even in Overlapping mode, 1 (default) covers most smoothing-driven gaps,
        // 2-3 = aggressive (handles heavy potrace smoothing at the cost of a tinted halo
        // on the smaller layer's silhouette).
        var overlap = opts.Method == TraceMethod.Overlapping ? Math.Clamp(opts.OverlapRadius, 0, 3) : 0;
        var layers = new List<(SKColor Color, int PixelArea, string PathSvg)>();
        for (var i = 0; i < palette.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (pbm, area) = BuildLayerPbm(assignment, src.Width, src.Height, (byte)i, overlap);
            if (area == 0) continue;
            var traced = await RunPotraceOnBmpAsync(potracePath, pbm, args, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(traced)) continue;
            var pathFragment = ExtractPathFragment(traced);
            if (string.IsNullOrEmpty(pathFragment)) continue;
            layers.Add((palette[i], area, pathFragment));
        }

        if (layers.Count == 0) return null;
        // Largest area first → smaller details overprint bigger backgrounds.
        layers.Sort((a, b) => b.PixelArea.CompareTo(a.PixelArea));

        var sb = new System.Text.StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"<?xml version=\"1.0\" standalone=\"no\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {src.Width} {src.Height}\">\n");

        // No background rect: when the user picks IgnoreColor they expect those pixels to
        // become TRANSPARENT in the output (Illustrator semantics). Repainting them with
        // the ignored colour would make Ignore Color a visual no-op. The
        // <see cref="TraceOptions.Transparency"/> flag is reserved for honouring source
        // PNG alpha (a separate Illustrator concept), not for toggling this behaviour.

        foreach (var (color, _, pathSvg) in layers)
        {
            // potrace emits paths in inverted-Y coords scaled by 0.1; the wrapping <g>
            // here matches what potrace's own monochrome wrapper does so the per-layer
            // paths align in our composite SVG.
            var hex = $"{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
            if (opts.AutoGrouping)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"  <g id=\"layer-{hex}\" fill=\"#{hex}\" transform=\"translate(0,{src.Height}) scale(0.1,-0.1)\">\n    {pathSvg}\n  </g>\n");
            }
            else
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"  <g fill=\"#{hex}\" transform=\"translate(0,{src.Height}) scale(0.1,-0.1)\">\n    {pathSvg}\n  </g>\n");
            }
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>Build the palette per <see cref="TraceOptions.Palette"/>:
    /// • Limited → quantize to <see cref="TraceOptions.ColorCount"/> top-frequency buckets.
    /// • Automatic → quantize but drop colours whose share &lt; 2% (elbow-prune).
    /// • FullTone → no quantization; bucket every unique source RGB up to <see cref="FullToneLayerCap"/>.
    ///
    /// When <see cref="TraceOptions.IgnoreColor"/> is set we request one EXTRA bucket from
    /// the quantizer so that after the ignored bucket is dropped downstream, the user still
    /// gets the count they asked for. Without this bump, picking "3 Colors" + Ignore
    /// Background gives only 2 effective layers — counter-intuitive for the typical "logo
    /// has 3 colours, drop the bg" workflow.</summary>
    private static List<SKColor> BuildPalette(SKBitmap src, TraceOptions opts)
    {
        var n = Math.Clamp(opts.ColorCount, 2, 64);
        var requested = opts.IgnoreColor.HasValue ? Math.Min(n + 1, 64) : n;
        return opts.Palette switch
        {
            TracePalette.FullTone => FullTonePalette(src),
            TracePalette.Automatic => QuantizePalette(src, requested, minSharePercent: 2.0),
            _ => QuantizePalette(src, requested, minSharePercent: 0.0),
        };
    }

    /// <summary>FullTone palette: every distinct source RGB (alpha-thresholded), capped at
    /// <see cref="FullToneLayerCap"/> by frequency. Photos trivially blow past this cap;
    /// the cap keeps the trace runtime tractable while still giving denser gradient
    /// coverage than Limited mode.</summary>
    private static List<SKColor> FullTonePalette(SKBitmap src)
    {
        var hist = new Dictionary<int, int>();
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                if (c.Alpha < 16) continue;
                var key = (c.Red << 16) | (c.Green << 8) | c.Blue;
                hist[key] = hist.TryGetValue(key, out var prev) ? prev + 1 : 1;
            }
        }
        return hist.OrderByDescending(kv => kv.Value)
                   .Take(FullToneLayerCap)
                   .Select(kv => new SKColor(
                       (byte)((kv.Key >> 16) & 0xFF),
                       (byte)((kv.Key >> 8) & 0xFF),
                       (byte)(kv.Key & 0xFF),
                       255))
                   .ToList();
    }

    /// <summary>Cheap palette quantization: sample every Kth pixel (~10K samples), bucket
    /// into a fixed-stride histogram, take the top-n by frequency. 16 buckets per channel
    /// → 16³=4096 buckets total, 16-unit resolution. Earlier we used 4 buckets per channel
    /// (64 unit resolution) which was too coarse: a logo with black (RGB 0,0,0) and dark
    /// gray (RGB 40,40,40) collapsed both into the same bucket, so the palette had only
    /// ONE entry for "dark colours". When the user then asked Ignore Color = gray with
    /// a low tolerance, the merged-black-and-gray entry got dropped and the black text
    /// disappeared from the trace. 16 buckets keeps black and dark-gray separate while
    /// still merging anti-alias gradients into stable bucket centroids.
    /// <paramref name="minSharePercent"/> implements Automatic mode's elbow-prune — colours
    /// below the share threshold are dropped after the top-n cut.</summary>
    private static List<SKColor> QuantizePalette(SKBitmap src, int n, double minSharePercent)
    {
        var stride = Math.Max(1, src.Width * src.Height / 10000);
        const int bucketsPerChannel = 16;
        var hist = new Dictionary<int, (int Count, int R, int G, int B)>();
        var idx = 0;
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++, idx++)
            {
                if (idx % stride != 0) continue;
                var c = src.GetPixel(x, y);
                if (c.Alpha < 16) continue;
                var br = c.Red   * bucketsPerChannel / 256;
                var bg = c.Green * bucketsPerChannel / 256;
                var bb = c.Blue  * bucketsPerChannel / 256;
                var key = (br << 8) | (bg << 4) | bb;
                if (hist.TryGetValue(key, out var prev))
                    hist[key] = (prev.Count + 1, prev.R + c.Red, prev.G + c.Green, prev.B + c.Blue);
                else
                    hist[key] = (1, c.Red, c.Green, c.Blue);
            }
        }
        var totalSamples = hist.Values.Sum(v => (long)v.Count);
        if (totalSamples == 0) return new List<SKColor>();
        var minCount = (long)Math.Ceiling(totalSamples * minSharePercent / 100.0);
        return hist.OrderByDescending(kv => kv.Value.Count)
                   .Take(n)
                   .Where(kv => kv.Value.Count >= minCount)
                   .Select(kv => new SKColor(
                       (byte)(kv.Value.R / kv.Value.Count),
                       (byte)(kv.Value.G / kv.Value.Count),
                       (byte)(kv.Value.B / kv.Value.Count),
                       255))
                   .ToList();
    }

    /// <summary>Apply <paramref name="passes"/> rounds of <see cref="BoxBlur3x3"/> to
    /// <paramref name="src"/>, returning a fresh owned bitmap. <c>passes &lt;= 0</c> just
    /// returns a Copy() so the caller's <c>using</c> always has something to dispose without
    /// worrying about whether the input bitmap is its own to release. Intermediate buffers
    /// are disposed inline so memory peak stays at 2x the source.</summary>
    private static SKBitmap ApplyPreBlur(SKBitmap src, int passes)
    {
        if (passes <= 0) return src.Copy();
        SKBitmap? current = null;
        var input = src;
        for (var i = 0; i < passes; i++)
        {
            var next = BoxBlur3x3(input);
            current?.Dispose();
            current = next;
            input = next;
        }
        return current!;
    }

    /// <summary>Cheap 3x3 box blur on the source bitmap, used to smooth anti-aliased edge
    /// pixels into their neighbourhood before quantisation. Border pixels average over the
    /// available 5/3 neighbours (no clamping/mirror — keeps the implementation tight; edge
    /// pixels are rarely the visual focus of a trace anyway). Alpha is averaged alongside
    /// RGB so a transparent border doesn't bleed pseudo-colour into nearby pixels.</summary>
    private static SKBitmap BoxBlur3x3(SKBitmap src)
    {
        var w = src.Width;
        var h = src.Height;
        var dst = new SKBitmap(w, h, src.ColorType, src.AlphaType);
        for (var y = 0; y < h; y++)
        {
            var y0 = Math.Max(0, y - 1);
            var y1 = Math.Min(h - 1, y + 1);
            for (var x = 0; x < w; x++)
            {
                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(w - 1, x + 1);
                int sumR = 0, sumG = 0, sumB = 0, sumA = 0, count = 0;
                for (var ny = y0; ny <= y1; ny++)
                {
                    for (var nx = x0; nx <= x1; nx++)
                    {
                        var c = src.GetPixel(nx, ny);
                        sumR += c.Red; sumG += c.Green; sumB += c.Blue; sumA += c.Alpha;
                        count++;
                    }
                }
                dst.SetPixel(x, y, new SKColor(
                    (byte)(sumR / count), (byte)(sumG / count),
                    (byte)(sumB / count), (byte)(sumA / count)));
            }
        }
        return dst;
    }

    /// <summary>Collapse <paramref name="src"/> to its luminance diagonal so the colour
    /// pipeline picks only gray buckets. Keeps alpha intact so transparent pixels still
    /// drop out of the palette / mask. Caller owns disposal of the returned bitmap.</summary>
    private static SKBitmap ToGrayscale(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                var luma = (byte)Math.Clamp((int)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue), 0, 255);
                dst.SetPixel(x, y, new SKColor(luma, luma, luma, c.Alpha));
            }
        }
        return dst;
    }

    /// <summary>Constant tolerance for palette layer membership. Pixels within this many
    /// units (per channel) of a palette colour are assigned to that layer. Distinct from
    /// <see cref="TraceOptions.IgnoreTolerance"/> which only affects the IgnoreColor
    /// predicate — using IgnoreTolerance here would couple the user-facing "ignore this
    /// background colour" tolerance to layer-assignment width, which makes layer overlap
    /// blow up when the user widens the ignore tolerance.</summary>
    private const int PaletteMatchTolerance = 32;

    /// <summary>Sentinel "no layer" value in palette-assignment arrays — used for pixels
    /// that match IgnoreColor or are too transparent to belong to any layer.</summary>
    private const byte NoLayer = 255;

    /// <summary>Build the per-pixel palette assignment: <c>byte[]</c> sized W×H where each
    /// entry is the index into <paramref name="palette"/> the pixel belongs to, or
    /// <see cref="NoLayer"/> when the pixel should not be traced at all (alpha &lt; 16
    /// → preserve source transparency; matches IgnoreColor → user-driven drop).
    /// <para>Method semantics:
    /// <list type="bullet">
    /// <item><b>Overlapping</b>: tolerance-based first-match. Iterate the palette in order,
    /// pick the first colour within <see cref="PaletteMatchTolerance"/> per channel.
    /// Pixels outside the tolerance fall through to nearest-match so no pixel is
    /// silently dropped (which would leave white holes).</item>
    /// <item><b>Abutting</b>: nearest-match by Euclidean RGB distance, period. Each pixel
    /// belongs to exactly one layer with no overlap on the seams — the geometry is
    /// strictly partitioned across layers.</item>
    /// </list>
    /// </para>
    /// The alpha guard here is the fix for the "transparent hole filled with the wrong
    /// colour" bug — without it, a pixel like (255,255,255,0) (transparent white) would
    /// match the white palette layer and paint over what should remain a hole.</summary>
    private static byte[] BuildPaletteAssignment(SKBitmap src, IReadOnlyList<SKColor> palette, TraceOptions opts)
    {
        var w = src.Width;
        var h = src.Height;
        var assignment = new byte[w * h];
        var ignore = opts.IgnoreColor;
        var hasIgnore = ignore.HasValue;
        var ignoreSk = hasIgnore ? ToSk(ignore!.Value) : default;
        var ignoreTol = opts.IgnoreTolerance;
        var abutting = opts.Method == TraceMethod.Abutting;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = src.GetPixel(x, y);
                var idx = y * w + x;

                if (c.Alpha < 16) { assignment[idx] = NoLayer; continue; }

                if (hasIgnore
                    && Math.Abs(c.Red - ignoreSk.Red) <= ignoreTol
                    && Math.Abs(c.Green - ignoreSk.Green) <= ignoreTol
                    && Math.Abs(c.Blue - ignoreSk.Blue) <= ignoreTol)
                {
                    assignment[idx] = NoLayer;
                    continue;
                }

                if (abutting)
                {
                    assignment[idx] = NearestPaletteIndex(c, palette);
                }
                else
                {
                    // Overlapping: tolerance-first, fall through to nearest if none matches.
                    var found = NoLayer;
                    for (byte i = 0; i < palette.Count; i++)
                    {
                        var p = palette[i];
                        if (Math.Abs(c.Red - p.Red) <= PaletteMatchTolerance
                         && Math.Abs(c.Green - p.Green) <= PaletteMatchTolerance
                         && Math.Abs(c.Blue - p.Blue) <= PaletteMatchTolerance)
                        { found = i; break; }
                    }
                    assignment[idx] = found != NoLayer ? found : NearestPaletteIndex(c, palette);
                }
            }
        }
        return assignment;
    }

    /// <summary>3x3 majority-filter pass over the per-pixel palette assignment. Each
    /// non-transparent pixel adopts the most common assignment in its 3x3 neighbourhood (self
    /// included), but only when the majority is strict (≥5/9) — strictness avoids "flipping
    /// thin features" (e.g. a 1px-wide line through a uniform field would have 6 background
    /// neighbours and lose every interior pixel without the threshold).
    /// <para><see cref="NoLayer"/> pixels are never overwritten and are excluded from the
    /// majority count, so transparent regions stay transparent and we never hallucinate
    /// content into a hole the user wanted preserved.</para>
    /// <para>One pass smooths single-pixel zigzag (the dominant artefact of anti-aliased
    /// edges getting bucketed inconsistently). Multiple passes would smooth wider noise but
    /// risk eating real fine detail; we keep it at one for now and let users dial in
    /// <see cref="TraceOptions.NoisePx"/> if they need more.</para></summary>
    private static byte[] SmoothAssignment(byte[] assignment, int w, int h)
    {
        if (w < 3 || h < 3) return assignment;
        var result = new byte[w * h];
        Array.Copy(assignment, result, assignment.Length);
        Span<int> counts = stackalloc int[256];
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var idx = y * w + x;
                if (assignment[idx] == NoLayer) continue;
                counts.Clear();
                var rowAbove = (y - 1) * w;
                var rowMid = y * w;
                var rowBelow = (y + 1) * w;
                counts[assignment[rowAbove + x - 1]]++;
                counts[assignment[rowAbove + x]]++;
                counts[assignment[rowAbove + x + 1]]++;
                counts[assignment[rowMid + x - 1]]++;
                counts[assignment[rowMid + x]]++;
                counts[assignment[rowMid + x + 1]]++;
                counts[assignment[rowBelow + x - 1]]++;
                counts[assignment[rowBelow + x]]++;
                counts[assignment[rowBelow + x + 1]]++;
                byte majIdx = assignment[idx];
                var majCount = 0;
                for (var i = 0; i < 256; i++)
                {
                    if (i == NoLayer) continue;
                    if (counts[i] > majCount) { majCount = counts[i]; majIdx = (byte)i; }
                }
                if (majCount >= 5) result[idx] = majIdx;
            }
        }
        return result;
    }

    private static byte NearestPaletteIndex(SKColor c, IReadOnlyList<SKColor> palette)
    {
        byte best = 0;
        var bestD = int.MaxValue;
        for (byte i = 0; i < palette.Count; i++)
        {
            var p = palette[i];
            var dr = c.Red - p.Red;
            var dg = c.Green - p.Green;
            var db = c.Blue - p.Blue;
            var d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Build a binary PBM where bit=1 iff the assignment array maps that pixel to
    /// <paramref name="layerIndex"/>, optionally extended by a <paramref name="dilateRadius"/>
    /// pixel collar that overlaps adjacent layers (the pixel belongs to a DIFFERENT layer
    /// but is within <paramref name="dilateRadius"/> 4-connected steps of a
    /// <paramref name="layerIndex"/> pixel). Returns the PBM bytes plus the count of fg
    /// pixels for layer Z-order sorting downstream.
    /// <para>Dilation never bleeds into <see cref="NoLayer"/> pixels, so transparent regions
    /// (alpha &lt; 16, IgnoreColor matches) stay transparent in the output. That's what
    /// keeps interior holes intact (e.g. the lion's eye / mouth on the Peugeot logo) while
    /// still closing seams between adjacent colour regions — exactly Illustrator's
    /// Overlapping behaviour.</para>
    /// <para>Radius is implemented as N rounds of 1-pixel dilation rather than a single
    /// distance-N test, both for simplicity and because it lets the dilation propagate
    /// through narrow channels of non-NoLayer pixels (e.g. a 1-pixel-wide stroke between
    /// two regions) without special-casing connectivity.</para></summary>
    private static (byte[] Bytes, int Area) BuildLayerPbm(byte[] assignment, int w, int h, byte layerIndex, int dilateRadius)
    {
        // Step 1: build the initial bool mask of "pixel belongs to this layer".
        var inLayer = new bool[w * h];
        for (var i = 0; i < assignment.Length; i++)
        {
            inLayer[i] = assignment[i] == layerIndex;
        }

        // Step 2: N rounds of 1-pixel dilation into adjacent non-NoLayer pixels. Each round
        // expands the layer's silhouette by one pixel; iterate to grow further. Reading from
        // a snapshot ('prev') is essential — without it, expansion would propagate within
        // a single round, exploding into a flood-fill of every connected non-NoLayer area.
        for (var r = 0; r < dilateRadius; r++)
        {
            var prev = (bool[])inLayer.Clone();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var idx = y * w + x;
                    if (prev[idx]) continue;
                    if (assignment[idx] == NoLayer) continue;
                    if ((y > 0 && prev[(y - 1) * w + x])
                     || (y < h - 1 && prev[(y + 1) * w + x])
                     || (x > 0 && prev[y * w + x - 1])
                     || (x < w - 1 && prev[y * w + x + 1]))
                    {
                        inLayer[idx] = true;
                    }
                }
            }
        }

        // Step 3: pack the mask into a P4 PBM document (header + bit-packed rows).
        var rowBytes = (w + 7) / 8;
        var header = System.Text.Encoding.ASCII.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"P4\n{w} {h}\n"));
        var buf = new byte[header.Length + rowBytes * h];
        Buffer.BlockCopy(header, 0, buf, 0, header.Length);
        var p = header.Length;
        var area = 0;
        for (var y = 0; y < h; y++)
        {
            byte cur = 0;
            var bit = 7;
            for (var x = 0; x < w; x++)
            {
                if (inLayer[y * w + x])
                {
                    cur |= (byte)(1 << bit);
                    area++;
                }
                bit--;
                if (bit < 0) { buf[p++] = cur; cur = 0; bit = 7; }
            }
            if (bit != 7) { buf[p++] = cur; }
        }
        return (buf, area);
    }

    private static async Task<string?> RunPotraceOnBmpAsync(string potracePath, byte[] bmpBytes, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = potracePath,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start potrace");
        await p.StandardInput.BaseStream.WriteAsync(bmpBytes, ct).ConfigureAwait(false);
        p.StandardInput.Close();
        var svg = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode == 0 ? svg : null;
    }

    /// <summary>Pull the <c>&lt;path&gt;</c> body out of a full potrace SVG so it can be
    /// stacked inside a multi-layer composite. potrace's per-trace output looks like
    /// <c>&lt;svg…&gt;&lt;g…&gt;&lt;path…/&gt;&lt;/g&gt;&lt;/svg&gt;</c> — we strip the
    /// outer wrappers since we provide our own.</summary>
    private static string ExtractPathFragment(string fullSvg)
    {
        var pathStart = fullSvg.IndexOf("<path", StringComparison.Ordinal);
        if (pathStart < 0) return string.Empty;
        var pathEnd = fullSvg.LastIndexOf("</g>", StringComparison.Ordinal);
        if (pathEnd > pathStart) return fullSvg.Substring(pathStart, pathEnd - pathStart);
        // Fallback: <path .../> self-closed
        var selfClose = fullSvg.IndexOf("/>", pathStart, StringComparison.Ordinal);
        return selfClose > 0 ? fullSvg.Substring(pathStart, selfClose - pathStart + 2) : string.Empty;
    }

    /// <summary>Decode the input PNG and emit a binary PBM (P4) document for the
    /// monochrome path, plus the average colour of the pixels we classified as foreground
    /// (<paramref name="avgFgColor"/>). The fg-colour is used downstream to recolour
    /// potrace's hardcoded black silhouette so a "white icon on dark bg" trace ends up
    /// as a WHITE silhouette in the SVG instead of a black one — matches the user
    /// expectation that the trace preserves the source's tone.</summary>
    private static byte[]? ConvertToPbm(byte[] inputPng, TraceOptions opts, out SKColor avgFgColor)
    {
        avgFgColor = SKColors.Black;
        using var src = SKBitmap.Decode(inputPng);
        if (src is null) return null;
        var pbm = EncodePbm(src, target: null, opts, tolerance: opts.IgnoreTolerance, out avgFgColor);
        return pbm;
    }

    /// <summary>Build a binary PBM. When <paramref name="target"/> is non-null the
    /// predicate is "pixel ≈ target within tolerance" (multi-colour layer path). When
    /// null the predicate is luminance-based: explicit cutoff via
    /// <see cref="TraceOptions.Threshold"/> if set (0-255), otherwise auto-polarity via
    /// <see cref="ClassifyMonoFg"/> (legacy behaviour). Pixels matching
    /// <see cref="TraceOptions.IgnoreColor"/> are forced to background regardless of mode
    /// so the eyedropper / "ignore background colour" feature works in both paths.</summary>
    private static byte[] EncodePbm(SKBitmap src, SKColor? target, TraceOptions opts, int tolerance)
        => EncodePbm(src, target, opts, tolerance, out _);

    private static byte[] EncodePbm(SKBitmap src, SKColor? target, TraceOptions opts, int tolerance, out SKColor avgFgColor)
    {
        var w = src.Width;
        var h = src.Height;
        var rowBytes = (w + 7) / 8;
        var header = System.Text.Encoding.ASCII.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"P4\n{w} {h}\n"));
        var buf = new byte[header.Length + rowBytes * h];
        Buffer.BlockCopy(header, 0, buf, 0, header.Length);
        var p = header.Length;
        long sumR = 0, sumG = 0, sumB = 0, fgCount = 0;

        // For mono path: use explicit threshold if 0-255, else fall back to auto-polarity.
        // Threshold is mapped 0-255 → luma cutoff: pixel < threshold = foreground (dark
        // ink on light background, the standard reading direction).
        var explicitThreshold = target is null
            && opts.Threshold >= 0 && opts.Threshold <= 255
            ? opts.Threshold
            : AutoThreshold;
        var monoFg = target is null && explicitThreshold == AutoThreshold ? ClassifyMonoFg(src) : null;
        var t = target ?? default;
        var hasTarget = target is not null;

        var ignore = opts.IgnoreColor;
        var hasIgnore = ignore.HasValue;
        var ignoreSk = hasIgnore ? ToSk(ignore!.Value) : default;
        var ignoreTol = opts.IgnoreTolerance;

        for (var y = 0; y < h; y++)
        {
            byte cur = 0;
            var bit = 7;
            for (var x = 0; x < w; x++)
            {
                var c = src.GetPixel(x, y);

                // IgnoreColor wins regardless of path: matched pixels are background.
                bool ignored = hasIgnore
                    && Math.Abs(c.Red - ignoreSk.Red) <= ignoreTol
                    && Math.Abs(c.Green - ignoreSk.Green) <= ignoreTol
                    && Math.Abs(c.Blue - ignoreSk.Blue) <= ignoreTol;

                bool fg;
                if (ignored)
                {
                    fg = false;
                }
                else if (hasTarget)
                {
                    fg = Math.Abs(c.Red - t.Red) <= tolerance
                      && Math.Abs(c.Green - t.Green) <= tolerance
                      && Math.Abs(c.Blue - t.Blue) <= tolerance;
                }
                else if (hasIgnore)
                {
                    // BW mode + IgnoreColor: the user picked the colour to drop, so the
                    // silhouette is everything-not-ignored regardless of luma. Without this
                    // branch, a "white icon on dark bg" with Ignore Color = dark would also
                    // exclude the white pixels (luma 255 ≥ threshold = not foreground)
                    // leaving a blank trace. Skipping the threshold here matches the
                    // user's mental model: "drop this colour, trace what's left".
                    fg = c.Alpha >= 16;
                }
                else if (explicitThreshold != AutoThreshold)
                {
                    if (c.Alpha < 16) fg = false;
                    else
                    {
                        var luma = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                        fg = luma < explicitThreshold;
                    }
                }
                else
                {
                    fg = monoFg!(c);
                }

                if (fg)
                {
                    cur |= (byte)(1 << bit);
                    sumR += c.Red;
                    sumG += c.Green;
                    sumB += c.Blue;
                    fgCount++;
                }
                bit--;
                if (bit < 0)
                {
                    buf[p++] = cur;
                    cur = 0;
                    bit = 7;
                }
            }
            // Pad partial trailing byte at row end.
            if (bit != 7) { buf[p++] = cur; }
        }
        avgFgColor = fgCount > 0
            ? new SKColor((byte)(sumR / fgCount), (byte)(sumG / fgCount), (byte)(sumB / fgCount), 255)
            : SKColors.Black;
        return buf;
    }

    /// <summary>Pick the foreground predicate for monochrome trace. We sample the image,
    /// count pixels above and below the 50% luminance threshold, then return a predicate
    /// that classifies the MINORITY cluster as foreground. Why: potrace traces the FG bits
    /// as filled paths, and the user's intent is almost always "trace the icon I see" —
    /// which is the smaller object on top of a larger background, regardless of whether
    /// the icon is brighter or darker than its surround. Hard-coding "luma &lt; 128 = fg"
    /// (the original behaviour) inverts whenever the icon is brighter than the background
    /// (e.g. a bright green Play on a darker grey UI), producing a "filled square with
    /// icon-shaped hole". Auto-polarity by majority count fixes that in the common case
    /// without exposing a manual Invert toggle.
    /// Transparent pixels never count toward either bucket and never trace.</summary>
    private static Func<SKColor, bool> ClassifyMonoFg(SKBitmap src)
    {
        var w = src.Width;
        var h = src.Height;
        // Sample stride: ~10K samples is enough to determine majority polarity reliably
        // and keeps the pre-pass under a millisecond on 4K screenshots.
        var stride = Math.Max(1, w * h / 10000);
        long darkCount = 0;
        long lightCount = 0;
        var idx = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++, idx++)
            {
                if (idx % stride != 0) continue;
                var c = src.GetPixel(x, y);
                if (c.Alpha < 16) continue;
                var luma = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                if (luma < 128) darkCount++;
                else lightCount++;
            }
        }
        // Minority = foreground. Tie → dark = fg (matches the classic "dark icon on light
        // background" interpretation).
        var darkIsForeground = darkCount <= lightCount;
        return c =>
        {
            if (c.Alpha < 16) return false;
            var luma = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            return darkIsForeground ? luma < 128 : luma >= 128;
        };
    }

    /// <summary>Map the parameter snapshot onto potrace's CLI flags. <c>-t</c> turdsize
    /// (despeckle), <c>-a</c> alphamax (corner smoothness), <c>-O</c> opttolerance (curve
    /// optimization), <c>-k</c> threshold (mono only), <c>-n</c> disables curve-to-line
    /// snapping. Always emits <c>--svg --output - -</c> so we read SVG from stdout and
    /// PBM from stdin. Threshold is mono-only; <c>-k</c> ignored by potrace on already-
    /// binary PBM input from the colour layers' mask (their pixels are 0/255 by
    /// construction so the cutoff is a no-op there).</summary>
    private static string BuildPotraceArgs(TraceOptions o)
    {
        var alphamax = 1.3 * (1.0 - o.CornersPercent / 100.0);
        // Paths → potrace -O (opttolerance). Illustrator: low Paths = tight fit (more
        // anchors, follows pixels), high Paths = looser fit (fewer anchors, smoother).
        // potrace -O matches that direction: low = no merging, high = aggressive merging.
        // Previously the mapping was inverted (1 - p/100), so high Paths sent the LOWER
        // tolerance — which together with potrace's silent "opttolerance ≤ alphamax" cap
        // (default Corners=75 → alphamax=0.325) made the slider feel completely dead:
        // most of its travel landed above the cap and got clamped to the same value.
        // 0-100 → 0.0-1.5 gives a real range when Corners is low/moderate; at high Corners
        // (sharp corners → low alphamax) Paths' effective range compresses, but that
        // coupling is intrinsic to potrace and the user can dial Corners down to recover.
        var opttolerance = o.PathsPercent / 100.0 * 1.5;
        var args = new System.Text.StringBuilder("--svg --output - ");
        args.Append(System.Globalization.CultureInfo.InvariantCulture, $"-t {o.NoisePx} ");
        args.Append(System.Globalization.CultureInfo.InvariantCulture, $"-a {alphamax:F3} ");
        args.Append(System.Globalization.CultureInfo.InvariantCulture, $"-O {opttolerance:F3} ");
        if (o.Mode == TraceMode.BlackAndWhite)
            args.Append(System.Globalization.CultureInfo.InvariantCulture, $"-k {o.Threshold / 255.0:F3} ");
        if (!o.SnapCurvesToLines)
            args.Append("-n ");
        args.Append('-');
        return args.ToString();
    }

    /// <summary>Bridge <see cref="System.Drawing.Color"/> → <see cref="SKColor"/>.
    /// <see cref="TraceOptions.IgnoreColor"/> uses System.Drawing for serializability
    /// (System.Text.Json handles it natively); the tracer needs SkiaSharp's flavour for
    /// per-pixel comparisons.</summary>
    private static SKColor ToSk(System.Drawing.Color c) => new(c.R, c.G, c.B, c.A);
}
