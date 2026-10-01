using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AresToys.App.Services.Logging;

/// <summary>Last-chance exception handling. Without it any unhandled exception kills the process
/// silently and the in-memory Debug log dies with it, so a crash report from a user carries no
/// stack at all. Every unhandled exception is written to
/// <c>%LocalAppData%\AresToys-Data\Logs\crash-*.log</c> before anything else happens.
/// <para>Known WPF framework bugs that are harmless to survive (see <see cref="IsKnownWpfBug"/>)
/// are marked handled so the app keeps running. Everything else still terminates the process as
/// before: swallowing arbitrary exceptions could leave the app in a corrupt state.</para></summary>
public static class CrashReporter
{
    private const int MaxLogFiles = 20;
    private static readonly object FileLock = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AresToys-Data", "Logs");

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var survivable = IsKnownWpfBug(e.Exception);
        Write(survivable ? "Dispatcher (known WPF bug, ignored)" : "Dispatcher (fatal)", e.Exception);
        if (survivable) e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Write(e.IsTerminating ? "AppDomain (fatal)" : "AppDomain", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Doesn't terminate the process on modern .NET; logged so fire-and-forget failures
        // stop disappearing without a trace.
        Write("Unobserved task", e.Exception);
        e.SetObserved();
    }

    /// <summary>NullReferenceException thrown inside WPF's UI Automation peers (e.g.
    /// <c>ItemAutomationPeer.GetNameCore</c> during <c>ContextLayoutManager.fireAutomationEvents</c>).
    /// It fires when a list's items change, or its window closes, while a UIA client (Narrator,
    /// touch keyboard, Phone Link, password managers...) is listening; under heavy system load
    /// the deferred layout pass widens the race. Nothing of ours is on the stack and the
    /// automation tree is simply rebuilt on the next layout pass.</summary>
    internal static bool IsKnownWpfBug(Exception ex)
    {
        if (ex is not NullReferenceException) return false;
        var stack = ex.StackTrace;
        return stack is not null
            && stack.Contains("System.Windows.Automation.Peers.", StringComparison.Ordinal)
            && stack.Contains("System.Windows.ContextLayoutManager.fireAutomationEvents", StringComparison.Ordinal);
    }

    private static void Write(string source, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"Time:    {now:yyyy-MM-dd HH:mm:ss.fff}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"Source:  {source}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"Version: {typeof(CrashReporter).Assembly.GetName().Version}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"OS:      {Environment.OSVersion}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"Runtime: {Environment.Version}").AppendLine();
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");

            lock (FileLock)
            {
                Directory.CreateDirectory(LogDirectory);
                var name = string.Create(CultureInfo.InvariantCulture,
                    $"crash-{now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
                File.WriteAllText(Path.Combine(LogDirectory, name), sb.ToString());
                PruneOldLogs();
            }
        }
        catch
        {
            // Never let the crash reporter itself throw from a last-chance handler.
        }
    }

    private static void PruneOldLogs()
    {
        var files = new DirectoryInfo(LogDirectory).GetFiles("crash-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxLogFiles);
        foreach (var f in files)
        {
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
