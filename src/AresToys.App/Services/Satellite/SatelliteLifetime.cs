using System.Diagnostics;

namespace AresToys.App.Services.Satellite;

/// <summary>Lets the satellite follow the main app: when that process is gone the satellite has nobody to serve.</summary>
public static class SatelliteLifetime
{
    private const int PollMs = 1000;

    /// <summary>Polls the process every second and calls <paramref name="onGone"/> once when it has exited or no
    /// longer exists. <paramref name="mainPid"/> 0 watches nothing. Dispose the result to stop watching.</summary>
    public static IDisposable WatchMainProcess(int mainPid, Action onGone)
    {
        var watcher = new Watcher(mainPid, onGone);
        watcher.Start();
        return watcher;
    }

    private sealed class Watcher(int mainPid, Action onGone) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public void Start()
        {
            if (mainPid == 0) return;
            _ = Task.Run(() => PollAsync(_cts.Token));
        }

        private async Task PollAsync(CancellationToken token)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
                while (!IsGone())
                {
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            onGone();
        }

        private bool IsGone()
        {
            try
            {
                using var process = Process.GetProcessById(mainPid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true; // no process with that id
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // cannot be queried: it is alive, the pipe closing is the other way the satellite learns
            }
        }

        public void Dispose() => _cts.Cancel();
    }
}
