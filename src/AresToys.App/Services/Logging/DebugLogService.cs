using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Logging;

/// <summary>Singleton ring-buffer of recent log entries. Both the file-less in-app Debug tab and
/// the (future) on-disk log writer pull from here. Caps at <see cref="Capacity"/> entries to keep
/// memory bounded — the oldest entry is dropped on overflow. Thread-safe at the append site
/// (logger callbacks come from any thread); the ObservableCollection is always mutated on the UI
/// thread so WPF bindings stay valid.</summary>
public sealed class DebugLogService
{
    public const int Capacity = 5000;

    private readonly object _lock = new();

    /// <summary>Bound directly by the Debug tab. Mutations marshalled to the dispatcher because
    /// WPF requires UI-thread access for bound collections.</summary>
    public ObservableCollection<DebugLogEntry> Entries { get; } = [];

    public void Append(DebugLogEntry entry)
    {
        var app = Application.Current;
        if (app is null)
        {
            // App not started yet (early startup) or already shutting down. Drop silently —
            // logs at those points are usually not interesting and the alternative is races.
            return;
        }

        // ALWAYS dispatch via BeginInvoke, even when the caller is already on the UI thread.
        // Reason: if a log call fires from WITHIN a binding pipeline (e.g. WPF's binding update
        // emits its own log, or our subscriber's ScrollIntoView triggers logging), running Add
        // synchronously would re-enter ObservableCollection during its in-flight CollectionChanged
        // notification — InvalidOperationException ("Cannot change ObservableCollection during a
        // CollectionChanged event"). Deferring to the next dispatcher tick fully decouples the
        // mutation from any caller's stack frame, eliminating that re-entry class entirely.
        try
        {
            app.Dispatcher.BeginInvoke(() => DoAppend(entry));
        }
        catch
        {
            // Dispatcher torn down (rare, mostly during shutdown). Drop the line.
        }
    }

    private void DoAppend(DebugLogEntry entry)
    {
        try
        {
            lock (_lock)
            {
                Entries.Add(entry);
                while (Entries.Count > Capacity)
                {
                    Entries.RemoveAt(0);
                }
            }
        }
        catch
        {
            // Defensive: swallow any unexpected exception so a malformed log line never crashes
            // the app. Whatever broke is independently logged via the console provider.
        }
    }

    public void Clear()
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(Clear);
            return;
        }
        lock (_lock) Entries.Clear();
    }

    /// <summary>Render every entry as a single newline-joined block — used by the Copy command in
    /// the Debug tab so the user can paste a full session into a bug report.</summary>
    public string FormatAll()
    {
        var sb = new StringBuilder();
        DebugLogEntry[] snapshot;
        lock (_lock) snapshot = Entries.ToArray();
        foreach (var e in snapshot)
        {
            sb.Append(e.Format()).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>One captured log line. Immutable so a snapshot copy is safe to pass across threads.</summary>
public sealed record DebugLogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception)
{
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string LevelDisplay => Level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    /// <summary>Color the Level cell paints in. Static frozen brushes per level so each entry
    /// reuses the same Brush instance — bound directly via <c>Foreground="{Binding LevelBrush}"</c>
    /// in the Debug tab template (no converter needed).</summary>
    public Brush LevelBrush => Level switch
    {
        LogLevel.Error or LogLevel.Critical => _errorBrush,
        LogLevel.Warning => _warningBrush,
        LogLevel.Debug or LogLevel.Trace => _debugBrush,
        _ => _infoBrush,
    };

    private static readonly Brush _errorBrush   = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly Brush _warningBrush = Frozen(0xFF, 0xC8, 0x57);
    private static readonly Brush _debugBrush   = Frozen(0x88, 0x88, 0x88);
    private static readonly Brush _infoBrush    = Frozen(0xDD, 0xDD, 0xDD);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public string Format()
    {
        var sb = new StringBuilder()
            .Append(TimestampDisplay).Append(' ')
            .Append(LevelDisplay).Append(' ')
            .Append('[').Append(Category).Append("] ")
            .Append(Message);
        if (!string.IsNullOrEmpty(Exception))
        {
            sb.Append('\n').Append(Exception);
        }
        return sb.ToString();
    }
}
