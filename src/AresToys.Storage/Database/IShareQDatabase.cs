
namespace AresToys.Storage.Database;

public interface IAresToysDatabase : IAsyncDisposable
{
    /// <summary>Open and migrate the database. Idempotent.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Take exclusive use of the one connection, waiting for any operation in progress.
    /// Hold the lease for the whole operation (commands, readers, transactions) and dispose it
    /// before raising events or calling another method that acquires one: leases are not
    /// re-entrant. Storage internals only, do not expose to consumers.</summary>
    ValueTask<DatabaseLease> AcquireAsync(CancellationToken cancellationToken = default);
}
