using Microsoft.Data.Sqlite;
using AresToys.Storage.Tests.Fixtures;
using Xunit;

namespace AresToys.Storage.Tests.Database;

public class AresToysDatabaseTests
{
    [Fact]
    public async Task InitializeAsync_OnFreshDirectory_CreatesSchemaAtLatestVersion()
    {
        await using var fixture = await new TempDatabaseFixture().InitializeAsync();

        var connection = fixture.Database.GetOpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_version;";
        var version = (long)(await cmd.ExecuteScalarAsync())!;

        // Migration001 (consolidated v1 schema) + Migration002 (adds items.label + rebuilds FTS)
        // + Migration003 (adds items.pin_sort_order for user-controlled pinned reordering)
        // + Migration004 (adds items.trigger for the Key Sequences module).
        Assert.Equal(4, version);
    }

    [Fact]
    public async Task InitializeAsync_CreatesItemsTableWithExpectedColumns()
    {
        await using var fixture = await new TempDatabaseFixture().InitializeAsync();

        var columns = await ListColumnsAsync(fixture.Database.GetOpenConnection(), "items");

        Assert.Contains("id", columns);
        Assert.Contains("kind", columns);
        Assert.Contains("source", columns);
        Assert.Contains("created_at", columns);
        Assert.Contains("payload", columns);
        Assert.Contains("payload_size", columns);
        Assert.Contains("blob_ref", columns);
        Assert.Contains("uploaded_url", columns);
        Assert.Contains("search_text", columns);
        // Categories table is part of the consolidated v1 schema — regression coverage so a
        // future migration list shuffle can't silently drop the column the popup's right-click
        // "Move to" depends on.
        Assert.Contains("category", columns);
        // Added in Migration002 — CopyQ-style per-item label (optional name shown in the row
        // title in place of the auto-derived snippet).
        Assert.Contains("label", columns);
        // Added in Migration003 — user-controlled sort order applied to pinned rows.
        Assert.Contains("pin_sort_order", columns);
        // Added in Migration004 — Key Sequences module trigger token.
        Assert.Contains("trigger", columns);
    }

    [Fact]
    public async Task InitializeAsync_CreatesFtsVirtualTableAndTriggers()
    {
        await using var fixture = await new TempDatabaseFixture().InitializeAsync();
        var connection = fixture.Database.GetOpenConnection();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE name = 'items_fts' AND type = 'table';";
        var ftsName = await cmd.ExecuteScalarAsync();

        Assert.Equal("items_fts", ftsName);

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'items_%';";
        var triggerCount = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(3, triggerCount);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await using var fixture = await new TempDatabaseFixture().InitializeAsync();

        await fixture.Database.InitializeAsync(CancellationToken.None);
        await fixture.Database.InitializeAsync(CancellationToken.None);

        var connection = fixture.Database.GetOpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var rowCount = (long)(await cmd.ExecuteScalarAsync())!;
        // One row per applied migration (v1 consolidated + v2 label / FTS rebuild + v3 pin_sort_order + v4 trigger).
        Assert.Equal(4, rowCount);
    }

    [Fact]
    public async Task GetOpenConnection_BeforeInitialize_Throws()
    {
        await using var fixture = new TempDatabaseFixture();

        Assert.Throws<InvalidOperationException>(() => fixture.Database.GetOpenConnection());
    }

    private static async Task<List<string>> ListColumnsAsync(SqliteConnection connection, string tableName)
    {
        var columns = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }
}
