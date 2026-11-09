using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

/// <summary>Adds the per-item <c>label</c> column (CopyQ "Notes" equivalent) and rebuilds
/// the FTS5 index so search matches across both <c>search_text</c> and <c>label</c>.</summary>
public sealed class Migration002AddItemLabel : IMigration
{
    public int TargetVersion => 2;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSql()
    {
        const string ResourceName = "AresToys.Storage.Database.SchemaSql.migration_v2.sql";
        using var stream = typeof(Migration002AddItemLabel).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
