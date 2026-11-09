using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AresToys.App.Services.Logging;

/// <summary>Writes <c>startup-trace.log</c> next to the crash logs: when each startup phase ended, how long every
/// wormhole took to load, and every stretch where the UI thread did not answer for more than 100 ms during the
/// first minute. It exists to find what makes the PC lag while AresToys starts. Each run overwrites the file.</summary>
public static class StartupTrace
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly TimeSpan ProcessAge = ReadProcessAge();
    private static string? _path;

    public static string FilePath => _path ??= Path.Combine(CrashReporter.LogDirectory, "startup-trace.log");

    /// <summary>Starts the file and the UI stall watcher. Call once, first thing in OnStartup.</summary>
    public static void Begin(Dispatcher ui)
    {
        try
        {
            Directory.CreateDirectory(CrashReporter.LogDirectory);
            lock (Gate) File.WriteAllText(FilePath, $"AresToys startup trace, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, process age at start {ProcessAge.TotalMilliseconds:0} ms{Environment.NewLine}");
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var thread = new Thread(() => WatchUi(ui)) { IsBackground = true, Name = "StartupTrace.UiWatch" };
        thread.Start();
    }

    /// <summary>One line: milliseconds since the process started and the phase that just ended.</summary>
    public static void Mark(string phase) => Write($"{(ProcessAge + Clock.Elapsed).TotalMilliseconds,8:0} ms  {phase}");

    /// <summary>Runs <paramref name="work"/> and logs how long it took.</summary>
    public static async Task MeasureAsync(string what, Func<Task> work)
    {
        var start = Stopwatch.StartNew();
        try { await work().ConfigureAwait(true); }
        finally { Write($"{(ProcessAge + Clock.Elapsed).TotalMilliseconds,8:0} ms  {what} took {start.ElapsedMilliseconds} ms"); }
    }

    private static void WatchUi(Dispatcher ui)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(60))
        {
            var asked = Stopwatch.StartNew();
            try { ui.Invoke(() => { }, DispatcherPriority.Normal); }
            catch (Exception) { return; }
            var waited = asked.ElapsedMilliseconds;
            if (waited > 100) Write($"{(ProcessAge + Clock.Elapsed).TotalMilliseconds,8:0} ms  UI thread blocked for {waited} ms");
            Thread.Sleep(50);
        }
    }

    private static void Write(string line)
    {
        try { lock (Gate) File.AppendAllText(FilePath, line + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static TimeSpan ReadProcessAge()
    {
        try { return DateTime.Now - Process.GetCurrentProcess().StartTime; }
        catch (Exception) { return TimeSpan.Zero; }
    }
}
