using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database;

/// <summary>
/// Exclusive use of the one SQLite connection, handed out by
/// <see cref="IAresToysDatabase.AcquireAsync"/>. Hold it for the whole operation that touches
/// the connection (commands, readers, transactions) and dispose it before raising events or
/// calling anything that acquires a lease of its own: leases are not re-entrant, a second
/// acquisition on the same flow waits forever.
/// </summary>
public sealed class DatabaseLease : IDisposable, IAsyncDisposable
{
    private SqliteConnection? _connection;
    private Action? _release;

    internal DatabaseLease(SqliteConnection connection, Action release)
    {
        _connection = connection;
        _release = release;
    }

    /// <summary>The shared connection. Valid until the lease is disposed.</summary>
    public SqliteConnection Connection
        => _connection ?? throw new ObjectDisposedException(nameof(DatabaseLease));

    /// <summary>Release the connection. Safe to call more than once.</summary>
    public void Dispose()
    {
        _connection = null;
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
