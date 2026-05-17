using Microsoft.Extensions.Logging;

namespace AresToys.App.Tests.KeySequences.Fakes;

/// <summary>Accumulating <see cref="ILogger{T}"/> for assertions. Captures formatted messages
/// per level so a test can verify a warning was emitted without depending on the exact wording.</summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        Entries.Add((logLevel, msg, exception));
    }

    public bool HasLevel(LogLevel level) => Entries.Any(e => e.Level == level);

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
