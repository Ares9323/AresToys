using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

public sealed class MigrationRunner
{
    private readonly IReadOnlyList<IMigration> _migrations;

    public MigrationRunner(IEnumerable<IMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        _migrations = migrations.OrderBy(m => m.TargetVersion).ToList();
    }

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var current = await ReadCurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var migration in _migrations)
        {
            if (migration.TargetVersion <= current) continue;
            await migration.ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
            current = migration.TargetVersion;
        }
    }

    private static async Task<int> ReadCurrentVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version' LIMIT 1;";
        var found = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (found is null) return 0;

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT MAX(version) FROM schema_version;";
        var value = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
