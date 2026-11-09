using NAudio.Wave;

namespace AresToys.AI.Dictation;

/// <summary>Records the microphone as 16 kHz mono samples, the format whisper reads. One take at a time:
/// <see cref="Start"/>, then <see cref="Stop"/> returns everything heard in between.</summary>
public sealed class MicRecorder : IDisposable
{
    public const int SampleRate = 16000;
    /// <summary>A take is capped so a stuck key cannot grow the buffer for ever (3 minutes).</summary>
    public const int MaxSeconds = 180;

    private readonly object _gate = new();
    private readonly List<float> _samples = [];
    private WaveInEvent? _input;
    private volatile float _level;

    /// <summary>Loudness of the latest buffer, 0..1, for the indicator.</summary>
    public float Level => _level;

    public bool IsRecording => _input is not null;

    /// <summary>Names of the recording devices, in the order their indexes are used by <see cref="Start"/>.</summary>
    public static IReadOnlyList<string> Devices()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return names;
    }

    /// <summary>Starts a take on the device with that name, or the system default when it is empty or not found.</summary>
    public void Start(string? deviceName)
    {
        if (_input is not null) return;

        lock (_gate) _samples.Clear();
        var device = 0;
        if (!string.IsNullOrEmpty(deviceName))
        {
            var names = Devices();
            for (var i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], deviceName, StringComparison.OrdinalIgnoreCase)) { device = i; break; }
            }
        }

        var input = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        input.DataAvailable += OnData;
        input.StartRecording();
        _input = input;
    }

    /// <summary>Ends the take and returns its samples (empty when none was running).</summary>
    public float[] Stop()
    {
        var input = _input;
        _input = null;
        if (input is null) return [];

        input.DataAvailable -= OnData;
        try { input.StopRecording(); } catch (InvalidOperationException) { /* the device went away mid take */ }
        input.Dispose();
        _level = 0;

        lock (_gate)
        {
            var result = _samples.ToArray();
            _samples.Clear();
            return result;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        var chunk = new float[count];
        double energy = 0;
        for (var i = 0; i < count; i++)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            chunk[i] = sample;
            energy += sample * sample;
        }
        // RMS, stretched so ordinary speech reaches well into the range.
        _level = count == 0 ? 0 : Math.Min(1f, (float)Math.Sqrt(energy / count) * 6f);

        lock (_gate)
        {
            if (_samples.Count < MaxSeconds * SampleRate) _samples.AddRange(chunk);
        }
    }

    public void Dispose() => _ = Stop();
}
