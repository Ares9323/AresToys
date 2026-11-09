using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

/// <summary>Adds the per-item <c>pin_sort_order</c> column powering the user-controlled
/// reorder gesture on the pinned strip (drag-drop + chevron buttons).</summary>
public sealed class Migration003AddPinSortOrder : IMigration
{
    public int TargetVersion => 3;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSql()
    {
        const string ResourceName = "AresToys.Storage.Database.SchemaSql.migration_v3.sql";
        using var stream = typeof(Migration003AddPinSortOrder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
