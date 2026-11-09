using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

/// <summary>Adds the index behind the copy-time dedup probe (<c>ItemStore.TryDedupAsync</c>):
/// <c>idx_items_dedup</c> on (kind, payload_size, created_at DESC) restricted to live rows, so
/// the probe finds its candidate through the index instead of walking every live row of the same
/// kind. Additive: no existing column, index or row is altered.</summary>
public sealed class Migration006AddDedupIndex : IMigration
{
    public int TargetVersion => 6;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSql()
    {
        const string ResourceName = "AresToys.Storage.Database.SchemaSql.migration_v6.sql";
        using var stream = typeof(Migration006AddDedupIndex).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
