using Microsoft.Data.Sqlite;

namespace ShareQ.Storage.Database.Migrations;

/// <summary>v2 — adds the <c>image_effect_presets</c> table. Additive: existing v1 installs
/// upgrade in place by running the v2 SQL on top.</summary>
public sealed class Migration002ImageEffectPresets : IMigration
{
    public int TargetVersion => 2;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = LoadSchemaSql();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LoadSchemaSql()
    {
        const string ResourceName = "ShareQ.Storage.Database.SchemaSql.migration_v2_image_effect_presets.sql";
        using var stream = typeof(Migration002ImageEffectPresets).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
