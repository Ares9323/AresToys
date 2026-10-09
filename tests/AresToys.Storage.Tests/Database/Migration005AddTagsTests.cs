using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.Storage.Database;
using AresToys.Storage.Database.Migrations;
using AresToys.Storage.Options;
using AresToys.Storage.Paths;
using Xunit;

namespace AresToys.Storage.Tests.Database;

/// <summary>Migration005 (clipboard tags) applied on top of a populated v4 database: the
/// existing row survives, stays findable through the rebuilt FTS index, and the new tables
/// are in place with their cascades.</summary>
public class Migration005AddTagsTests
{
    [Fact]
    public async Task AppliedToV4Database_AddsTagTables_AndKeepsSearchWorking()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "AresToys.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        var options = new StorageOptions { RootDirectoryOverride = rootDir };
        var paths = new StoragePathResolver(Microsoft.Extensions.Options.Options.Create(options));

        try
        {
            var v4Migrations = new IMigration[]
            {
                new Migration001InitialSchema(),
                new Migration002AddItemLabel(),
                new Migration003AddPinSortOrder(),
                new Migration004AddItemTrigger()
            };
            long insertedId;
            await using (var v4Db = new AresToysDatabase(paths, new MigrationRunner(v4Migrations), NullLogger<AresToysDatabase>.Instance))
            {
                await v4Db.InitializeAsync(CancellationToken.None);
                var conn = v4Db.GetOpenConnection();
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO items (kind, source, created_at, payload, payload_size, search_text, label)
                    VALUES ('Text', 'Clipboard', $now, X'00', 1, 'legacy content', 'old label');
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                insertedId = (long)(await insert.ExecuteScalarAsync())!;
            }

            var fullMigrations = v4Migrations.Append(new Migration005AddTags()).ToArray();
            await using var v5Db = new AresToysDatabase(paths, new MigrationRunner(fullMigrations), NullLogger<AresToysDatabase>.Instance);
            await v5Db.InitializeAsync(CancellationToken.None);
            var c = v5Db.GetOpenConnection();

            Assert.Equal(5L, await ScalarAsync(c, "SELECT MAX(version) FROM schema_version;"));
            Assert.Equal(1L, await ScalarAsync(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tags';"));
            Assert.Equal(1L, await ScalarAsync(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='item_tags';"));
            // Pre-existing row is still indexed by content and label after the FTS rebuild.
            Assert.Equal(insertedId, await ScalarAsync(c, "SELECT rowid FROM items_fts WHERE items_fts MATCH 'legacy';"));
            Assert.Equal(insertedId, await ScalarAsync(c, "SELECT rowid FROM items_fts WHERE items_fts MATCH 'label';"));
            Assert.True(await ScalarAsync(c, $"SELECT tag_text FROM items WHERE id = {insertedId};") is DBNull or null);
        }
        finally
        {
            // Pooled connections keep the database files open until their pool is cleared.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* lingering handles: best-effort cleanup */ }
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
