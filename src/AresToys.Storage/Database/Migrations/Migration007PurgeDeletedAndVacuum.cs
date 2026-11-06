using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

/// <summary>Purges the rows soft-deleted more than 24 h ago, then turns on incremental
/// auto_vacuum and runs one VACUUM so the file shrinks to the live data. The runner applies a
/// migration with no transaction, which VACUUM requires.</summary>
public sealed class Migration007PurgeDeletedAndVacuum : IMigration
{
    public int TargetVersion => 7;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSql()
    {
        const string ResourceName = "AresToys.Storage.Database.SchemaSql.migration_v7.sql";
        using var stream = typeof(Migration007PurgeDeletedAndVacuum).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
