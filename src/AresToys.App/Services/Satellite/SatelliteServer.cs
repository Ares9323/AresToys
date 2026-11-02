using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Satellite;

/// <summary>Main app side of the satellite pipe. It accepts the satellite, wraps the connection in a
/// <see cref="SatelliteChannel"/> and accepts again when the satellite drops. The pipe is open to the current user
/// only: an elevated process gets a default ACL that a normal integrity token of the same user cannot use, so the
/// user is granted access explicitly and nobody else is (not <c>PipeOptions.CurrentUserOnly</c>, whose check an
/// elevated owner fails).
/// <para>Two instances are allowed on the pipe: the connected one and the one waiting for the next satellite. A
/// satellite that connects while an older channel is still open replaces it; the older channel is disposed first.</para></summary>
public sealed class SatelliteServer : IDisposable
{
    private readonly ILogger? _logger;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private SatelliteChannel? _current;
    private Task? _loop;

    public SatelliteServer(ILogger? logger = null, string? pipeName = null)
    {
        _logger = logger;
        _pipeName = pipeName ?? SatelliteProtocol.PipeName();
    }

    /// <summary>The channel to the connected satellite; null while none is connected.</summary>
    public SatelliteChannel? Current => Volatile.Read(ref _current);

    /// <summary>Raised on every new connection, before the channel starts reading: register the request handlers here,
    /// so no request from the satellite can arrive before they exist.</summary>
    public event Action<SatelliteChannel>? Connected;

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                var channel = new SatelliteChannel(pipe, _logger);
                pipe = null; // owned by the channel now
                Interlocked.Exchange(ref _current, channel)?.Dispose();
                try { Connected?.Invoke(channel); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Satellite Connected handler failed"); }
                _ = Task.Run(() => ServeAsync(channel, token), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pipe?.Dispose();
                _logger?.LogWarning(ex, "Satellite pipe could not accept, retrying");
                try { await Task.Delay(1000, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ServeAsync(SatelliteChannel channel, CancellationToken token)
    {
        await channel.RunAsync(token).ConfigureAwait(false);
        Interlocked.CompareExchange(ref _current, null, channel);
        channel.Dispose();
    }

    private NamedPipeServerStream CreatePipe()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, security);
    }

    public void Dispose()
    {
        _cts.Cancel();
        Interlocked.Exchange(ref _current, null)?.Dispose();
        try { _loop?.Wait(500); }
        catch (AggregateException) { /* the loop only ends by cancellation */ }
        _cts.Dispose();
    }
}
