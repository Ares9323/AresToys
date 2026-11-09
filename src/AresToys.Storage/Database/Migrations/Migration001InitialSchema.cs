using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

public sealed class Migration001InitialSchema : IMigration
{
    public int TargetVersion => 1;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSchemaSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSchemaSql()
    {
        const string ResourceName = "AresToys.Storage.Database.SchemaSql.schema_v1.sql";
        using var stream = typeof(Migration001InitialSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
