using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Satellite;

/// <summary>Duplex message channel over any stream (the satellite pipe in the app, a pair of pipes or a loopback in
/// tests). Both sides can send requests that the other answers, and fire and forget events. One JSON object per
/// line. Sending never blocks the caller: lines go through an ordered queue that one writer task drains, so a slow
/// or absent peer cannot freeze a UI thread. Request handlers run on the thread pool, events are raised in order from
/// their own task; neither runs on the read loop, so a handler may itself call <see cref="RequestAsync"/>.</summary>
public sealed class SatelliteChannel : IDisposable
{
    private readonly Stream _stream;
    private readonly ILogger? _logger;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<SatelliteMessage> _events = Channel.CreateUnbounded<SatelliteMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, Func<JsonElement?, Task<JsonElement?>>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<SatelliteMessage?>> _pending = new();
    // Cancelled by Close so the read loop ends; never disposed because the read loop may still ask for its token.
    private readonly CancellationTokenSource _closeCts = new();
    private int _nextId;
    private int _closed;
    private int _disposed;

    public SatelliteChannel(Stream stream, ILogger? logger = null)
    {
        _stream = stream;
        _logger = logger;
        _reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = false };
        _ = Task.Run(WriteLoopAsync);
        _ = Task.Run(EventLoopAsync);
    }

    /// <summary>Raised for every event the other side published, in the order sent, off the read loop.</summary>
    public event Action<string, JsonElement?>? EventReceived;

    /// <summary>Raised once, when the channel ends (peer gone, stream broken or disposed).</summary>
    public event Action? Closed;

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>Registers the handler that answers requests called <paramref name="name"/>. A handler that throws
    /// answers the caller with Ok false and the exception message. A second registration replaces the first.</summary>
    public void Handle(string name, Func<JsonElement?, Task<JsonElement?>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[name] = handler;
    }

    /// <summary>Sends a request and waits for its answer. Null when the channel is closed, nobody answers within
    /// <paramref name="timeoutMs"/> or <paramref name="ct"/> is cancelled; never throws for those.</summary>
    public async Task<SatelliteMessage?> RequestAsync(string name, JsonElement? data = null, int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (!IsOpen) return null;

        var id = Interlocked.Increment(ref _nextId);
        var source = new TaskCompletionSource<SatelliteMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = source;
        try
        {
            // Closed between the first check and the registration: Close would not have seen this entry.
            if (!IsOpen) return null;
            if (!Enqueue(new SatelliteMessage(SatelliteProtocol.KindRequest, id, name, data))) return null;
            return await source.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends an event the other side raises through <see cref="EventReceived"/>. False when the channel is
    /// closed.</summary>
    public bool Publish(string name, JsonElement? data = null)
        => Enqueue(new SatelliteMessage(SatelliteProtocol.KindEvent, 0, name, data));

    /// <summary>The read loop. Returns when the stream ends, breaks or <paramref name="ct"/> is cancelled, and closes
    /// the channel.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closeCts.Token);
            while (true)
            {
                var line = await _reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                if (line is null) break;

                var message = SatelliteProtocol.Parse(line);
                if (message is not null) Dispatch(message);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The stream ended or broke: the channel closes below, like on a clean end.
        }
        finally
        {
            Close();
        }
    }

    private void Dispatch(SatelliteMessage message)
    {
        switch (message.Kind)
        {
            case SatelliteProtocol.KindResponse:
                if (_pending.TryGetValue(message.Id, out var source)) source.TrySetResult(message);
                break;
            case SatelliteProtocol.KindRequest:
                _ = Task.Run(() => AnswerAsync(message));
                break;
            case SatelliteProtocol.KindEvent:
                _events.Writer.TryWrite(message);
                break;
        }
    }

    private async Task AnswerAsync(SatelliteMessage request)
    {
        SatelliteMessage answer;
        if (!_handlers.TryGetValue(request.Name, out var handler))
        {
            answer = new SatelliteMessage(SatelliteProtocol.KindResponse, request.Id, request.Name, null, false, "Unknown request");
        }
        else
        {
            try
            {
                var data = await handler(request.Data).ConfigureAwait(false);
                answer = new SatelliteMessage(SatelliteProtocol.KindResponse, request.Id, request.Name, data);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Satellite request handler '{Name}' failed", request.Name);
                answer = new SatelliteMessage(SatelliteProtocol.KindResponse, request.Id, request.Name, null, false, ex.Message);
            }
        }
        Enqueue(answer);
    }

    private bool Enqueue(SatelliteMessage message)
        => IsOpen && _outbound.Writer.TryWrite(SatelliteProtocol.Serialize(message));

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _writer.WriteLineAsync(line.AsMemory()).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The peer is gone: nothing more can be written, so the channel closes.
            Close();
        }
    }

    private async Task EventLoopAsync()
    {
        await foreach (var message in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { EventReceived?.Invoke(message.Name, message.Data); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Satellite event handler '{Name}' failed", message.Name); }
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
        foreach (var pending in _pending.Values) pending.TrySetResult(null);
        _closeCts.Cancel();

        try { Closed?.Invoke(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Satellite Closed handler failed"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Close();
        _stream.Dispose();
    }
}
