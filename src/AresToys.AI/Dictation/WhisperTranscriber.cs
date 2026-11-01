using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace AresToys.AI.Dictation;

/// <summary>Turns a take into text with whisper.cpp. The model is read from its file on the first take and dropped
/// again after <see cref="IdleUnload"/> without one, so it does not keep its memory between dictations.</summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Timer _idleTimer;
    private WhisperFactory? _factory;
    private string? _loadedPath;

    public WhisperTranscriber()
    {
        _idleTimer = new Timer(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>How long the model stays loaded after a take. Zero drops it right after each take.</summary>
    public TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>True to stay on the processor; false lets the graphics card run it when it can (falling back to the processor).</summary>
    public bool CpuOnly { get; set; }

    /// <summary>Which native library runs the model, once one is loaded; empty before the first take.</summary>
    public static string RuntimeName => RuntimeOptions.LoadedLibrary?.ToString() ?? string.Empty;

    /// <summary>whisper reads little under about a second; shorter takes are padded with silence (the engine states it).</summary>
    private const int MinSamples = (int)(MicRecorder.SampleRate * 1.1);

    public async Task<string> TranscribeAsync(float[] samples, string modelPath, string languageCode, CancellationToken ct)
    {
        if (samples.Length == 0) return string.Empty;
        if (samples.Length < MinSamples) Array.Resize(ref samples, MinSamples);

        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            var factory = Load(modelPath);
            await using var processor = factory.CreateBuilder().WithLanguage(languageCode).Build();

            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }
            return Clean(text.ToString());
        }
        finally
        {
            if (IdleUnload > TimeSpan.Zero) _idleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _busy.Release();
            if (IdleUnload <= TimeSpan.Zero) Unload();
        }
    }

    /// <summary>Reads the model now, so a take can start recording only once transcription is ready. Does nothing when it is already loaded.</summary>
    public async Task PreloadAsync(string modelPath, CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            await Task.Run(() => Load(modelPath), ct).ConfigureAwait(false);
        }
        finally
        {
            if (IdleUnload > TimeSpan.Zero) _idleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _busy.Release();
        }
    }

    /// <summary>Whisper marks silence and noise with tags such as [BLANK_AUDIO] or a lone (music); those are not words.</summary>
    public static string Clean(string raw)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(raw, @"\[[^\]]*\]", string.Empty).Trim();
        if (text.Length > 2 && text[0] == '(' && text[^1] == ')' && text.IndexOf(')') == text.Length - 1) return string.Empty;
        return text;
    }

    private WhisperFactory Load(string modelPath)
    {
        lock (_gate)
        {
            if (_factory is not null && string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase)) return _factory;
            _factory?.Dispose();
            // The native library is chosen once per process, by the first factory: a change of processor
            // reaches the next start of the app.
            if (RuntimeOptions.LoadedLibrary is null)
                RuntimeOptions.RuntimeLibraryOrder = CpuOnly
                    ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                    : [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            _factory = WhisperFactory.FromPath(modelPath);
            _loadedPath = modelPath;
            return _factory;
        }
    }

    /// <summary>Drops the model from memory now (a take in progress keeps it).</summary>
    public void Unload()
    {
        if (!_busy.Wait(0)) return;
        try
        {
            lock (_gate)
            {
                _factory?.Dispose();
                _factory = null;
                _loadedPath = null;
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        lock (_gate)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}
