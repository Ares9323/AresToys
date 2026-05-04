using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Drawings;

/// <summary>Ported from ShareX (GPL v3) — ImageEffectsLib/Drawings/DrawParticles.cs. Scatters
/// random images from <see cref="ImageFolder"/> across the source bitmap, with optional size
/// jitter, angle jitter, opacity jitter, and a "no overlap" mode that retries placement up to
/// 1000 times before bailing on a particle. Used by Discord-style confetti / snow / hair /
/// emoji-bomb presets.</summary>
public sealed class DrawParticlesImageEffect : DrawingImageEffectBase
{
    public override string Id => "draw_particles";
    public override string Name => "Particles";

    public string ImageFolder { get; set; } = string.Empty;

    [EffectParameter(1, 1000, DisplayName = "Image count")]
    public int ImageCount { get; set; } = 1;

    public bool RandomSize { get; set; }
    [EffectParameter(1, 4000, DisplayName = "Random size min")]
    public int RandomSizeMin { get; set; } = 64;
    [EffectParameter(1, 4000, DisplayName = "Random size max")]
    public int RandomSizeMax { get; set; } = 128;

    public bool RandomAngle { get; set; }
    [EffectParameter(-360, 360, DisplayName = "Random angle min")]
    public int RandomAngleMin { get; set; }
    [EffectParameter(-360, 360, DisplayName = "Random angle max")]
    public int RandomAngleMax { get; set; } = 360;

    public bool RandomOpacity { get; set; }
    [EffectParameter(0, 100, DisplayName = "Random opacity min")]
    public int RandomOpacityMin { get; set; }
    [EffectParameter(0, 100, DisplayName = "Random opacity max")]
    public int RandomOpacityMax { get; set; } = 100;

    public bool NoOverlap { get; set; }
    [EffectParameter(-100, 100, DisplayName = "No-overlap offset")]
    public int NoOverlapOffset { get; set; }

    public bool EdgeOverlap { get; set; }

    /// <summary>If true, particles render onto a fresh empty canvas (so they end up BEHIND
    /// the source image after compositing). Otherwise they paint directly on top of the
    /// source. Mirrors ShareX's <c>Background</c> flag.</summary>
    public bool Background { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var folder = string.IsNullOrEmpty(ImageFolder) ? null : Environment.ExpandEnvironmentVariables(ImageFolder);
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return source.Copy();

        var files = System.IO.Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0) return source.Copy();

        // Background mode: empty canvas first, then source on top of the particles. Otherwise
        // particles paint over the source. ShareX uses the same fork.
        var result = Background
            ? new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            : source.Copy();

        // Cache decoded particle bitmaps so we don't re-decode the same file 1000 times when
        // ImageCount is high. Disposed at the end of the chain.
        var cache = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase);
        var placed = new List<SKRectI>();
        var rand = Random.Shared;

        try
        {
            using var canvas = new SKCanvas(result);
            for (var i = 0; i < ImageCount; i++)
            {
                var path = files[rand.Next(files.Length)];
                if (!cache.TryGetValue(path, out var sprite))
                {
                    try { sprite = SKBitmap.Decode(path); }
                    catch { sprite = null; }
                    if (sprite is not null) cache[path] = sprite;
                }
                if (sprite is null) continue;

                // Compute target size (with optional aspect-ratio preservation when one axis
                // shrinks more than the other under RandomSize).
                int width, height;
                if (RandomSize)
                {
                    var sizeMin = Math.Min(RandomSizeMin, RandomSizeMax);
                    var sizeMax = Math.Max(RandomSizeMin, RandomSizeMax);
                    var s = rand.Next(sizeMin, sizeMax + 1);
                    width = s;
                    height = s;
                    if (sprite.Width > sprite.Height)
                        height = (int)Math.Round(s * (sprite.Height / (double)sprite.Width));
                    else if (sprite.Width < sprite.Height)
                        width = (int)Math.Round(s * (sprite.Width / (double)sprite.Height));
                }
                else
                {
                    width = sprite.Width;
                    height = sprite.Height;
                }
                if (width < 1 || height < 1) continue;

                // Pick a random position. With EdgeOverlap, particles can poke off the canvas
                // edges; otherwise they're clamped fully inside.
                var minX = EdgeOverlap ? -width + 1 : 0;
                var minY = EdgeOverlap ? -height + 1 : 0;
                var maxX = source.Width - (EdgeOverlap ? 0 : width) - 1;
                var maxY = source.Height - (EdgeOverlap ? 0 : height) - 1;
                if (maxX < minX || maxY < minY) continue;

                SKRectI rect = default, overlapRect = default;
                var attempts = 0;
                var ok = false;
                while (attempts++ < 1000)
                {
                    var x = rand.Next(Math.Min(minX, maxX), Math.Max(minX, maxX) + 1);
                    var y = rand.Next(Math.Min(minY, maxY), Math.Max(minY, maxY) + 1);
                    rect = new SKRectI(x, y, x + width, y + height);
                    overlapRect = NoOverlap
                        ? Inflate(rect, NoOverlapOffset)
                        : rect;
                    if (!NoOverlap || !placed.Any(p => Intersects(p, overlapRect)))
                    {
                        ok = true;
                        break;
                    }
                }
                if (!ok) continue;
                placed.Add(overlapRect);

                // Optional alpha jitter — ShareX scales the overlay by Opacity/100. We do the
                // same via SKPaint.Color's alpha byte.
                byte alpha = 255;
                if (RandomOpacity)
                {
                    var oMin = Math.Min(RandomOpacityMin, RandomOpacityMax);
                    var oMax = Math.Max(RandomOpacityMin, RandomOpacityMax);
                    alpha = (byte)(255 * rand.Next(oMin, oMax + 1) / 100);
                }
                using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };

                if (RandomAngle)
                {
                    // Rotate around the particle's centre. We Save/Restore the canvas state
                    // so each particle's transform doesn't leak into the next.
                    var aMin = Math.Min(RandomAngleMin, RandomAngleMax);
                    var aMax = Math.Max(RandomAngleMin, RandomAngleMax);
                    var angle = rand.Next(aMin, aMax + 1);
                    var cx = rect.Left + (rect.Width / 2f);
                    var cy = rect.Top + (rect.Height / 2f);
                    canvas.Save();
                    canvas.Translate(cx, cy);
                    canvas.RotateDegrees(angle);
                    canvas.Translate(-cx, -cy);
                    canvas.DrawBitmap(sprite, new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), paint);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawBitmap(sprite, new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), paint);
                }
            }

            // Background mode: composite the source on top of the particles in a SECOND pass,
            // so the original image stays unmodified at the front and particles peek out
            // wherever the source has alpha < 255.
            if (Background)
            {
                canvas.DrawBitmap(source, 0, 0);
            }
        }
        finally
        {
            foreach (var sprite in cache.Values) sprite.Dispose();
        }
        return result;
    }

    /// <summary>SKRectI doesn't ship an Inflate that returns a new value; this small helper
    /// produces an offset rect (negative offset shrinks toward the centre, positive grows).</summary>
    private static SKRectI Inflate(SKRectI rect, int delta) =>
        new(rect.Left - delta, rect.Top - delta, rect.Right + delta, rect.Bottom + delta);

    /// <summary>Inclusive rect-rect intersection — manual because SKRectI's IntersectsWith
    /// uses strict inequality, and ShareX's no-overlap check treats edge-touching rects as
    /// overlapping so adjacent particles don't visually graze each other.</summary>
    private static bool Intersects(SKRectI a, SKRectI b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
