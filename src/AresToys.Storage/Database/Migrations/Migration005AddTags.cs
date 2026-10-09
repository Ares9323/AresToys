using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

/// <summary>Adds clipboard tags (issue #4): the <c>tags</c> definition table, the
/// <c>item_tags</c> many-to-many join (cascading on both sides), and the denormalised
/// <c>items.tag_text</c> column that feeds a rebuilt three-column FTS5 index. Additive: no
/// existing column is altered or dropped.</summary>
public sealed class Migration005AddTags : IMigration
{
    public int TargetVersion => 5;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSql()
    {
        const string ResourceName = "AresToys.Storage.Database.SchemaSql.migration_v5.sql";
        using var stream = typeof(Migration005AddTags).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
