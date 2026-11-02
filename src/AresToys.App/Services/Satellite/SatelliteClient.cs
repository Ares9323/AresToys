using System.IO;
using System.IO.Pipes;

namespace AresToys.App.Services.Satellite;

/// <summary>Satellite side of the pipe: connects to the main app's <see cref="SatelliteServer"/>.</summary>
public static class SatelliteClient
{
    private const int AttemptMs = 200;

    /// <summary>Retries every 200 ms until <paramref name="timeoutMs"/> has passed. Returns null when the main app
    /// cannot be reached or <paramref name="ct"/> is cancelled; otherwise the channel with its read loop already
    /// running.</summary>
    public static Task<SatelliteChannel?> ConnectAsync(int timeoutMs = 10000, string? pipeName = null, CancellationToken ct = default)
        => ConnectAsync(null, timeoutMs, pipeName, ct);

    /// <summary>Same as the overload without <paramref name="register"/>, which runs on the new channel before its read
    /// loop starts, so the request handlers the satellite registers there exist when the first request from the main
    /// app arrives.</summary>
    public static async Task<SatelliteChannel?> ConnectAsync(Action<SatelliteChannel>? register, int timeoutMs = 10000, string? pipeName = null, CancellationToken ct = default)
    {
        var name = pipeName ?? SatelliteProtocol.PipeName();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(AttemptMs, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                pipe.Dispose();
                if (Environment.TickCount64 >= deadline) return null;
                continue;
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return null;
            }

            var channel = new SatelliteChannel(pipe);
            register?.Invoke(channel);
            _ = Task.Run(() => channel.RunAsync(CancellationToken.None), CancellationToken.None);
            return channel;
        }

        return null;
    }
}
