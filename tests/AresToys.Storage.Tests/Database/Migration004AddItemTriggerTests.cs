using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.Storage.Database;
using AresToys.Storage.Database.Migrations;
using AresToys.Storage.Options;
using AresToys.Storage.Paths;
using Xunit;

namespace AresToys.Storage.Tests.Database;

/// <summary>Migration004 unit tests. Exercises both starting points: a fresh DB (runner
/// applies v1+v2+v3+v4 in sequence) and a DB previously initialised at v3 (runner skips
/// already-applied migrations and only adds the trigger column).</summary>
public class Migration004AddItemTriggerTests
{
    [Fact]
    public async Task AppliedToFreshDatabase_CreatesTriggerColumn()
    {
        // TempDatabaseFixture wires the full migration set including Migration004.
        await using var fx = await new Storage.Tests.Fixtures.TempDatabaseFixture().InitializeAsync();

        var conn = fx.Database.GetOpenConnection();
        Assert.Contains("trigger", await ListColumnsAsync(conn, "items"));
        Assert.Equal(4, await ReadSchemaVersionAsync(conn));
    }

    [Fact]
    public async Task AppliedToV3Database_AddsTriggerColumnWithoutDataLoss()
    {
        // Step 1: build a v3-state DB by running only migrations 1-3, insert a row, close.
        var rootDir = Path.Combine(Path.GetTempPath(), "AresToys.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        var options = new StorageOptions { RootDirectoryOverride = rootDir };
        var paths = new StoragePathResolver(Microsoft.Extensions.Options.Options.Create(options));

        long insertedId;
        try
        {
            var v3Migrations = new IMigration[]
            {
                new Migration001InitialSchema(),
                new Migration002AddItemLabel(),
                new Migration003AddPinSortOrder()
            };
            await using (var v3Db = new AresToysDatabase(paths, new MigrationRunner(v3Migrations), NullLogger<AresToysDatabase>.Instance))
            {
                await v3Db.InitializeAsync(CancellationToken.None);
                var conn = v3Db.GetOpenConnection();
                Assert.Equal(3, await ReadSchemaVersionAsync(conn));
                Assert.DoesNotContain("trigger", await ListColumnsAsync(conn, "items"));

                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO items (kind, source, created_at, payload, payload_size)
                    VALUES ('Text', 'Clipboard', $now, X'00', 1);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                insertedId = (long)(await insert.ExecuteScalarAsync())!;
            }

            // Step 2: re-open the same database with the full migration set including v4.
            // MigrationRunner should detect current = 3 and only apply Migration004.
            var fullMigrations = new IMigration[]
            {
                new Migration001InitialSchema(),
                new Migration002AddItemLabel(),
                new Migration003AddPinSortOrder(),
                new Migration004AddItemTrigger()
            };
            await using (var v4Db = new AresToysDatabase(paths, new MigrationRunner(fullMigrations), NullLogger<AresToysDatabase>.Instance))
            {
                await v4Db.InitializeAsync(CancellationToken.None);
                var conn = v4Db.GetOpenConnection();

                Assert.Equal(4, await ReadSchemaVersionAsync(conn));
                Assert.Contains("trigger", await ListColumnsAsync(conn, "items"));

                // Pre-existing row survived; the new trigger column defaults to NULL.
                await using var probe = conn.CreateCommand();
                probe.CommandText = "SELECT trigger FROM items WHERE id = $id;";
                probe.Parameters.AddWithValue("$id", insertedId);
                var triggerValue = await probe.ExecuteScalarAsync();
                Assert.True(triggerValue is DBNull or null);
            }
        }
        finally
        {
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* lingering handles — best-effort cleanup */ }
        }
    }

    private static async Task<List<string>> ListColumnsAsync(SqliteConnection connection, string tableName)
    {
        var columns = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_version;";
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? 0 : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
    }
}
