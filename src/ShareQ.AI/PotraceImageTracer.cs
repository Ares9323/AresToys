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
        var pbmBytes = ConvertToPbm(inputPng, opts);
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
        using var rawSrc = SKBitmap.Decode(inputPng);
        if (rawSrc is null) return null;

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

        var args = BuildPotraceArgs(opts);
        var layers = new List<(SKColor Color, int PixelArea, string PathSvg)>();
        foreach (var color in palette)
        {
            ct.ThrowIfCancellationRequested();
            var maskBmp = BuildMaskPbm(src, color, opts);
            if (maskBmp.Area == 0) continue;
            var traced = await RunPotraceOnBmpAsync(potracePath, maskBmp.Bytes, args, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(traced)) continue;
            // potrace emits a full <svg>…<g …><path …/></g></svg>; we want the <path> body
            // (and any nested elements inside the <g>) to compose into our outer SVG.
            var pathFragment = ExtractPathFragment(traced);
            if (string.IsNullOrEmpty(pathFragment)) continue;
            layers.Add((color, maskBmp.Area, pathFragment));
        }

        if (layers.Count == 0) return null;
        // Largest area first → smaller details overprint bigger backgrounds.
        layers.Sort((a, b) => b.PixelArea.CompareTo(a.PixelArea));

        // Method = Abutting: ideal output has each pixel belonging to exactly one layer
        // (no 1-px overlap). Implementing that cleanly requires post-processing the
        // potrace path geometry to clip overlap — non-trivial. For now we keep the
        // existing Overlapping behaviour for both methods and TODO the seam removal.
        // See plan task 1: "Prefer keeping the current behavior and noting Abutting for
        // a future iteration if it's non-trivial."
        // (Method intentionally unread here; placeholder for future Abutting work.)
        _ = opts.Method;

        var sb = new System.Text.StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"<?xml version=\"1.0\" standalone=\"no\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {src.Width} {src.Height}\">\n");

        // Transparency=off + IgnoreColor set → render an opaque background rect at SVG
        // bounds. Drawn first so all traced layers paint on top of it.
        if (!opts.Transparency && opts.IgnoreColor is { } bg)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"  <rect width=\"{src.Width}\" height=\"{src.Height}\" fill=\"#{bg.R:X2}{bg.G:X2}{bg.B:X2}\"/>\n");
        }

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
    /// • FullTone → no quantization; bucket every unique source RGB up to <see cref="FullToneLayerCap"/>.</summary>
    private static List<SKColor> BuildPalette(SKBitmap src, TraceOptions opts)
    {
        var n = Math.Clamp(opts.ColorCount, 2, 30);
        return opts.Palette switch
        {
            TracePalette.FullTone => FullTonePalette(src),
            TracePalette.Automatic => QuantizePalette(src, n, minSharePercent: 2.0),
            _ => QuantizePalette(src, n, minSharePercent: 0.0),
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
    /// into a fixed-stride 4³=64-bucket histogram, take the top-n by frequency. Good enough
    /// for icons / logos which dominate the use case; for photos the result is posterised
    /// anyway. <paramref name="minSharePercent"/> implements Automatic mode's elbow-prune
    /// — colours below the share threshold are dropped after the top-n cut.</summary>
    private static List<SKColor> QuantizePalette(SKBitmap src, int n, double minSharePercent)
    {
        var stride = Math.Max(1, src.Width * src.Height / 10000);
        const int bucketsPerChannel = 4;
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

    /// <summary>Build a binary PBM mask for a single palette colour and count the
    /// foreground (matched) pixels. The area count drives layer Z-order so smaller
    /// details paint on top of bigger backgrounds.</summary>
    private static (byte[] Bytes, int Area) BuildMaskPbm(SKBitmap src, SKColor target, TraceOptions opts)
    {
        var area = 0;
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                if (Math.Abs(c.Red - target.Red) <= PaletteMatchTolerance
                 && Math.Abs(c.Green - target.Green) <= PaletteMatchTolerance
                 && Math.Abs(c.Blue - target.Blue) <= PaletteMatchTolerance) area++;
            }
        }
        return (EncodePbm(src, target, opts, PaletteMatchTolerance), area);
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
    /// monochrome path. PBM is dead-simple — ASCII header (<c>P4\n&lt;w&gt; &lt;h&gt;\n</c>)
    /// followed by 1 bit per pixel packed MSB-first with rows padded to byte boundary.
    /// Foreground bit = 1 = black = what potrace traces.</summary>
    private static byte[]? ConvertToPbm(byte[] inputPng, TraceOptions opts)
    {
        using var src = SKBitmap.Decode(inputPng);
        if (src is null) return null;
        // Mono path: never use target colour matching (that's the multi-colour layer
        // codepath); we want a luminance-thresholded silhouette. EncodePbm keys off
        // target==null for that. opts carries Threshold + IgnoreColor so the encoder
        // can apply both there.
        return EncodePbm(src, target: null, opts, tolerance: opts.IgnoreTolerance);
    }

    /// <summary>Build a binary PBM. When <paramref name="target"/> is non-null the
    /// predicate is "pixel ≈ target within tolerance" (multi-colour layer path). When
    /// null the predicate is luminance-based: explicit cutoff via
    /// <see cref="TraceOptions.Threshold"/> if set (0-255), otherwise auto-polarity via
    /// <see cref="ClassifyMonoFg"/> (legacy behaviour). Pixels matching
    /// <see cref="TraceOptions.IgnoreColor"/> are forced to background regardless of mode
    /// so the eyedropper / "ignore background colour" feature works in both paths.</summary>
    private static byte[] EncodePbm(SKBitmap src, SKColor? target, TraceOptions opts, int tolerance)
    {
        var w = src.Width;
        var h = src.Height;
        var rowBytes = (w + 7) / 8;
        var header = System.Text.Encoding.ASCII.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"P4\n{w} {h}\n"));
        var buf = new byte[header.Length + rowBytes * h];
        Buffer.BlockCopy(header, 0, buf, 0, header.Length);
        var p = header.Length;

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

                if (fg) cur |= (byte)(1 << bit);
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
        var opttolerance = 1.0 - o.PathsPercent / 100.0;
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
