using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace AresToys.AI;

/// <summary>U2NetP (lite variant of U2Net, ~4.5 MB) ONNX model wrapped behind
/// <see cref="IBackgroundRemover"/>. The model takes a 320×320 RGB float tensor (values
/// 0-1) and outputs a single-channel saliency map of the same size (values 0-1, where 1 =
/// foreground). We resize the input down, run inference, threshold + resize the mask back
/// to the original dimensions, and composite as alpha onto the source image.
///
/// Session is created lazily on first use and reused for the lifetime of the instance —
/// model load is the expensive bit (~150 ms), per-image inference is comparatively fast
/// (~500 ms CPU, ~100 ms with DirectML).</summary>
public sealed class U2NetBackgroundRemover : IBackgroundRemover, IDisposable
{
    private const int ModelInputSize = 320;
    private const string ModelResourceName = "AresToys.AI.Models.u2netp.onnx";

    private readonly ILogger<U2NetBackgroundRemover> _logger;
    private InferenceSession? _session;
    private readonly object _sessionLock = new();
    private bool _disposed;

    public U2NetBackgroundRemover(ILogger<U2NetBackgroundRemover> logger)
    {
        _logger = logger;
    }

    /// <summary>Lazy-load the ONNX session from the embedded model resource. Tries
    /// DirectML first (GPU-accelerated on Win10/11 with any DX12 GPU), falls back to CPU
    /// when DirectML setup fails (older driver, headless environment, etc.). Locked so
    /// two concurrent first-calls don't both build a session.</summary>
    private InferenceSession GetOrCreateSession()
    {
        if (_session is not null) return _session;
        lock (_sessionLock)
        {
            if (_session is not null) return _session;
            var modelBytes = LoadEmbeddedModel();
            try
            {
                var options = new SessionOptions();
                options.AppendExecutionProvider_DML();
                _session = new InferenceSession(modelBytes, options);
                _logger.LogInformation("U2NetBackgroundRemover: session created with DirectML");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "U2NetBackgroundRemover: DirectML init failed, falling back to CPU");
                _session = new InferenceSession(modelBytes);
            }
            return _session;
        }
    }

    private static byte[] LoadEmbeddedModel()
    {
        using var stream = typeof(U2NetBackgroundRemover).Assembly
            .GetManifestResourceStream(ModelResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded model not found: {ModelResourceName}. Did you bundle u2netp.onnx?");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public Task<byte[]> RemoveBackgroundAsync(byte[] inputPng, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputPng);
        if (inputPng.Length == 0) return Task.FromResult(inputPng);
        // Run on a pool thread — inference is 100 ms-3 s, blocking the dispatcher would freeze
        // the UI for that long. Caller is responsible for marshalling the result back if it
        // needs to touch UI.
        return Task.Run(() => RemoveBackgroundCore(inputPng, cancellationToken), cancellationToken);
    }

    public Task<byte[]?> ExtractAlphaMaskAsync(byte[] inputPng, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputPng);
        if (inputPng.Length == 0) return Task.FromResult<byte[]?>(null);
        return Task.Run(() => ExtractMaskCore(inputPng, cancellationToken), cancellationToken);
    }

    private byte[] RemoveBackgroundCore(byte[] inputPng, CancellationToken ct)
    {
        using var input = SKBitmap.Decode(inputPng);
        if (input is null)
        {
            _logger.LogWarning("U2NetBackgroundRemover: input PNG failed to decode");
            return inputPng;
        }

        using var maskFull = RunInferenceAndResize(input, ct);
        if (maskFull is null) return inputPng;

        ct.ThrowIfCancellationRequested();
        using var output = ApplyMask(input, maskFull);
        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private byte[]? ExtractMaskCore(byte[] inputPng, CancellationToken ct)
    {
        using var input = SKBitmap.Decode(inputPng);
        if (input is null)
        {
            _logger.LogWarning("U2NetBackgroundRemover: input PNG failed to decode (mask extraction)");
            return null;
        }
        using var maskFull = RunInferenceAndResize(input, ct);
        if (maskFull is null) return null;
        using var image = SKImage.FromBitmap(maskFull);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Shared inference path used by both the composite and mask-only public methods.
    /// Loads the session, runs U2NetP at 320×320, resizes the saliency map back to source
    /// dimensions, returns a freshly owned <see cref="SKBitmap"/> grayscale-as-BGRA mask.
    /// Returns <c>null</c> when the session can't be created (model missing, ONNX broken)
    /// or the resize fails.</summary>
    private SKBitmap? RunInferenceAndResize(SKBitmap input, CancellationToken ct)
    {
        InferenceSession session;
        try { session = GetOrCreateSession(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "U2NetBackgroundRemover: session init failed");
            return null;
        }

        ct.ThrowIfCancellationRequested();
        using var resized = input.Resize(new SKImageInfo(ModelInputSize, ModelInputSize), SKSamplingOptions.Default);
        if (resized is null) return null;
        var inputTensor = ToTensor(resized);

        ct.ThrowIfCancellationRequested();
        // Build the input by name so we tolerate any future model whose input tensor isn't
        // called "input.1" (older U2Net releases use that, newer use just "input"). The
        // first metadata key is always the primary input for these single-input models.
        var inputName = session.InputMetadata.Keys.First();
        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(inputName, inputTensor),
        });
        var rawMask = results[0].AsTensor<float>();

        // Build a grayscale SKBitmap from the saliency map, then upsample to source dims.
        using var maskAtModel = MaskFromTensor(rawMask, ModelInputSize, ModelInputSize);
        // Ownership: Resize returns a NEW bitmap; if it fails we duplicate maskAtModel so the
        // caller always gets an owned bitmap to dispose without juggling fallbacks.
        return maskAtModel.Resize(new SKImageInfo(input.Width, input.Height), SKSamplingOptions.Default)
            ?? maskAtModel.Copy();
    }

    /// <summary>Convert an SKBitmap to the [1, 3, H, W] float tensor U2Net expects: BGRA →
    /// RGB, byte 0-255 → float 0-1, channel-first (R plane, G plane, B plane). Pre-pixel
    /// access via GetPixel keeps the code straight; for 320×320 input that's 102 K calls
    /// per image and stays well under the inference time budget.</summary>
    private static DenseTensor<float> ToTensor(SKBitmap bmp)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, bmp.Height, bmp.Width });
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                tensor[0, 0, y, x] = c.Red   / 255f;
                tensor[0, 1, y, x] = c.Green / 255f;
                tensor[0, 2, y, x] = c.Blue  / 255f;
            }
        }
        return tensor;
    }

    /// <summary>Build a grayscale SKBitmap from a saliency tensor. Values are 0-1 floats;
    /// we map to 0-255 byte alpha. SKBitmap is BGRA so we put the saliency in all four
    /// channels (greyscale white = subject, black = background).</summary>
    private static SKBitmap MaskFromTensor(Tensor<float> tensor, int width, int height)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = Math.Clamp(tensor[0, 0, y, x], 0f, 1f);
                var b = (byte)(v * 255);
                bmp.SetPixel(x, y, new SKColor(b, b, b, 255));
            }
        }
        return bmp;
    }

    /// <summary>Composite: keep source RGB, set alpha = mask.R (since the mask is greyscale,
    /// any channel works). Soft mask preserves edge feathering produced by the model.</summary>
    private static SKBitmap ApplyMask(SKBitmap source, SKBitmap mask)
    {
        var output = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var src = source.GetPixel(x, y);
                var m = mask.GetPixel(x, y).Red;
                output.SetPixel(x, y, new SKColor(src.Red, src.Green, src.Blue, m));
            }
        }
        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }
}
