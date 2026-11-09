namespace AresToys.App.Services.Recording;

/// <summary>Runs at most one operation at a time: a call made while one is in flight gets that
/// same task back instead of starting a second run. Once it completes (successfully or not)
/// the next call starts a fresh run. Used by <see cref="RecordingCoordinator"/> so repeated
/// stop requests during ffmpeg's finalize collapse into the one stop already running.</summary>
public sealed class SingleFlight
{
    private readonly object _gate = new();
    private Task? _inFlight;

    /// <summary>True while a run started by <see cref="RunAsync"/> hasn't completed yet.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _inFlight is { IsCompleted: false }; }
    }

    public Task RunAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } running) return running;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = tcs.Task;
        }
        // The work runs outside the lock: it may complete synchronously or call back in here.
        _ = RunCoreAsync(work, tcs);
        return tcs.Task;
    }

    private static async Task RunCoreAsync(Func<Task> work, TaskCompletionSource tcs)
    {
        try
        {
            await work().ConfigureAwait(false);
            tcs.TrySetResult();
        }
        catch (OperationCanceledException) { tcs.TrySetCanceled(); }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }
}
