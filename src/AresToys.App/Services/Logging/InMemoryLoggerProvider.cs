using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Logging;

/// <summary>Bridges Microsoft.Extensions.Logging into <see cref="DebugLogService"/>. Wired into
/// the host's logging builder alongside the console provider so every <c>ILogger&lt;T&gt;.Log…</c>
/// call across the codebase ends up visible in the in-app Debug tab — no per-class plumbing
/// required.</summary>
public sealed class InMemoryLoggerProvider(DebugLogService sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, sink);

    public void Dispose() { }
}

internal sealed class InMemoryLogger(string category, DebugLogService sink) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Trace gets dropped at the source — high-volume noise that would dominate the UI
    /// list. Anything Debug+ is recorded.</summary>
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        // Trim the category to the last segment — full namespace + class name eats horizontal
        // space in the UI without adding signal. Users can still see full categories via Copy.
        var shortCategory = category;
        var lastDot = category.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < category.Length - 1)
            shortCategory = category[(lastDot + 1)..];

        sink.Append(new DebugLogEntry(
            Timestamp: DateTimeOffset.UtcNow,
            Level: logLevel,
            Category: shortCategory,
            Message: msg,
            Exception: exception?.ToString()));
    }
}
