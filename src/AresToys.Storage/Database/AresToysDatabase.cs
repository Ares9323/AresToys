using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Database.Migrations;
using AresToys.Storage.Paths;

namespace AresToys.Storage.Database;

public sealed class AresToysDatabase : IAresToysDatabase
{
    private readonly IStoragePathResolver _paths;
    private readonly MigrationRunner _migrationRunner;
    private readonly ILogger<AresToysDatabase> _logger;
    private SqliteConnection? _connection;
    private bool _initialized;

    public AresToysDatabase(
        IStoragePathResolver paths,
        MigrationRunner migrationRunner,
        ILogger<AresToysDatabase> logger)
    {
        _paths = paths;
        _migrationRunner = migrationRunner;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        var dbPath = _paths.ResolveDatabasePath();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };

        _connection = new SqliteConnection(builder.ConnectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await _migrationRunner.ApplyAsync(_connection, cancellationToken).ConfigureAwait(false);
        _initialized = true;
        _logger.LogInformation("AresToys database initialized at {Path}", dbPath);
    }

    public SqliteConnection GetOpenConnection()
    {
        if (!_initialized || _connection is null)
            throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");
        return _connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            // The connection is pooled: without clearing its pool the file handles stay open
            // after dispose, so the database (and its -wal / -shm) can't be moved or deleted.
            SqliteConnection.ClearPool(_connection);
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        _initialized = false;
    }
}
