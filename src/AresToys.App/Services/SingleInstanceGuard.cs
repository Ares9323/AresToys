using System.IO;
using System.IO.Pipes;
using System.Text;

namespace AresToys.App.Services;

/// <summary>Mutex + named pipe combo that enforces "one AresToys process per user". The first
/// process owns the mutex (<see cref="IsPrimary"/> = true) and listens on the pipe; subsequent
/// launches detect they're not primary and forward the activation request through the pipe so
/// the primary can react (typically: bring the window to front, or import an .sxcu file passed
/// via Explorer file association).
///
/// Pipe protocol: a single UTF-8 message per connection, terminated by EOF (client closes after
/// writing). Special message <c>"show"</c> means "user re-launched without args, focus your
/// window"; anything else is treated as a payload and surfaced via
/// <see cref="AnotherInstanceStarted"/>'s message arg. Caller decides what to do with it (we use
/// the prefix <c>"sxcu:"</c> followed by an absolute file path for .sxcu imports).</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // Internal so the headless pick-color client can probe the mutex and reach the pipe without building a guard
    // (building one would take the mutex when no primary exists).
    internal const string MutexName = "Global\\AresToys.SingleInstance";
    internal const string PipeName = "AresToys.SingleInstance.Pipe";
    private const int DefaultConnectTimeoutMs = 1000;

    /// <summary>Conventional payload used by a re-launch with no args — primary brings UI front.</summary>
    public const string ShowMessage = "show";
    /// <summary>Prefix for "open a .sxcu file in the primary instance" messages — followed by
    /// the absolute file path. Kept here so producers / consumers stay in sync.</summary>
    public const string SxcuPrefix = "sxcu:";
    /// <summary>Prefix for "open a .sxie image-effects preset in the primary instance" — same
    /// shape as <see cref="SxcuPrefix"/>, dispatches to the image-effects import flow instead
    /// of the custom-uploader one.</summary>
    public const string SxiePrefix = "sxie:";
    /// <summary>Prefix for "upload this file via the primary instance" messages — used by the
    /// Explorer context-menu entry, which runs <c>arestoys.exe --upload "&lt;path&gt;"</c> from a
    /// brand-new process. The secondary forwards the absolute path here so the primary triggers
    /// its ManualUpload pipeline without relaunching the whole shell.</summary>
    public const string UploadPrefix = "upload:";

    /// <summary>Prefix for "create a wormhole pointing at this folder" — used by the Explorer
    /// folder right-click context-menu entry. Secondary instance forwards the folder path, the
    /// primary spawns a wormhole record + window with no dialog.</summary>
    public const string CreateWormholePrefix = "create-wormhole:";

    /// <summary>Prefix for "run this workflow on this file" messages from the Explorer image
    /// resize verbs (<c>arestoys.exe --resize &lt;workflowId&gt; "&lt;path&gt;"</c>, one process per
    /// selected file). The payload after the prefix is the workflow id, a TAB, then the absolute
    /// file path (workflow ids and Windows paths never contain a tab). The primary batches the
    /// messages that arrive close together into one workflow run and does NOT bring its main
    /// window forward for them.</summary>
    public const string ResizePrefix = "resize:";

    /// <summary>Prefix for "show the colour picker for another app" from <c>AresToys.exe --pick-color</c>. The
    /// payload is the reply pipe name, a TAB, the initial colour (RRGGBB or empty), a TAB, and the owner window
    /// handle in decimal (0 for none). The primary answers on the reply pipe and does NOT bring its main window
    /// forward.</summary>
    public const string PickColorPrefix = "pick-color:";

    /// <summary>"Show the NewWormholeDialog so the user can pick a source folder." Used by the
    /// Background variant of the shell verb (right-click on empty desktop / folder area) — there
    /// the right-click target is the folder being viewed, which is rarely what the user wants
    /// as the wormhole source (e.g. right-click on the desktop shouldn't auto-mirror Desktop).
    /// No payload; the dialog handles folder selection itself.</summary>
    public const string NewWormholeDialogMessage = "new-wormhole-dialog";

    private readonly Mutex? _mutex;
    private readonly bool _isPrimary;
    private readonly bool _isSatellite;
    private CancellationTokenSource? _serverCts;

    public bool IsPrimary => _isPrimary;

    /// <summary>Raised on the primary instance whenever a secondary launch reaches us. The
    /// string carries the message exchanged on the pipe — either <see cref="ShowMessage"/> or a
    /// <see cref="SxcuPrefix"/>-prefixed file path. Handlers run on a thread-pool thread; marshal
    /// to the UI dispatcher before touching WPF state.</summary>
    public event EventHandler<string>? AnotherInstanceStarted;

    /// <summary>A <c>--satellite</c> launch (the non elevated companion process the main app starts) is not an
    /// instance of its own: it takes neither the mutex nor the pipe, reports itself primary so startup goes on, and
    /// is never forwarded to the real primary.</summary>
    public SingleInstanceGuard(string[] args)
    {
        if (Satellite.SatelliteArgs.TryParse(args, out _))
        {
            _isSatellite = true;
            _isPrimary = true;
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);

        // A restart (Restart as administrator, Restart normally) starts the new process while the old one is
        // still shutting down: it stops the satellite before it releases the mutex. The new process waits for
        // the handover instead of forwarding "show" to an instance that is about to exit.
        if (!_isPrimary && args.Any(IsRestartArg))
        {
            try { _isPrimary = _mutex.WaitOne(RestartHandoverMs); }
            catch (AbandonedMutexException) { _isPrimary = true; }
        }
    }

    private const int RestartHandoverMs = 15_000;

    private static bool IsRestartArg(string arg) =>
        string.Equals(arg, ElevationService.RestartedElevatedArg, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, ElevationService.RestartedUnelevatedArg, StringComparison.OrdinalIgnoreCase);

    /// <summary>Send a payload to the primary instance. Convenience overload sends
    /// <see cref="ShowMessage"/> for the "just bring window to front" case.</summary>
    public Task NotifyExistingInstanceAsync(CancellationToken cancellationToken)
        => NotifyExistingInstanceAsync(ShowMessage, cancellationToken);

    public Task NotifyExistingInstanceAsync(string message, CancellationToken cancellationToken)
        => NotifyExistingInstanceAsync(message, DefaultConnectTimeoutMs, cancellationToken);

    /// <summary>Same as the overload without a timeout, but waits up to
    /// <paramref name="connectTimeoutMs"/> for the primary's pipe to appear. A primary that is
    /// still starting up only opens the pipe after its host is built, so callers that must not
    /// lose their message (one process per file from Explorer) pass a longer wait.</summary>
    public async Task NotifyExistingInstanceAsync(string message, int connectTimeoutMs, CancellationToken cancellationToken)
    {
        if (_isPrimary)
            throw new InvalidOperationException("This process owns the mutex; nothing to notify.");
        ArgumentNullException.ThrowIfNull(message);

        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        await client.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(message);
        await client.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        // Closing the client (via the using above) signals EOF to the server, which is how it
        // knows the message is fully received without us length-prefixing.
    }

    public void StartListening()
    {
        if (!_isPrimary || _isSatellite) return;
        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => RunServerAsync(_serverCts.Token));
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Read the full message until client closes the pipe (EOF). Using a MemoryStream
                // copy because the pipe stream isn't seekable and a StreamReader.ReadToEndAsync
                // would also work but pulls in extra buffering.
                using var ms = new MemoryStream();
                await server.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                var message = Encoding.UTF8.GetString(ms.ToArray());
                if (string.IsNullOrEmpty(message)) message = ShowMessage;

                AnotherInstanceStarted?.Invoke(this, message);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Pipe failures mid-cycle are non-fatal; loop unless cancellation requested.
            }
        }
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        _serverCts?.Dispose();
        if (_mutex is null) return;
        if (_isPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
