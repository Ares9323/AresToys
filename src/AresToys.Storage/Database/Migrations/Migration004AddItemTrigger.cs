using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

/// <summary>Adds the per-item <c>trigger</c> column used by the Key Sequences module
/// (Phase 1b). Storage-only metadata — the domain <c>Item</c> record is unchanged. Rows
/// default to NULL meaning "no trigger bound". Additive: no existing column is altered or
/// dropped, so any pre-v4 data round-trips untouched.</summary>
public sealed class Migration004AddItemTrigger : IMigration
{
    public int TargetVersion => 4;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSql()
    {
        const string ResourceName = "AresToys.Storage.Database.SchemaSql.migration_v4.sql";
        using var stream = typeof(Migration004AddItemTrigger).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
