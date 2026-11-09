using Microsoft.Data.Sqlite;
using AresToys.Storage.Database;

namespace AresToys.Storage.Items;

public sealed class SqliteTagStore : ITagStore
{
    private readonly IAresToysDatabase _database;

    public SqliteTagStore(IAresToysDatabase database) { _database = database; }

    public event EventHandler? Changed;

    /// <summary>Columns <see cref="MapTag"/> reads. The count only includes live (non-deleted)
    /// items: a soft-deleted item still holds its join rows until the hard delete, but the user
    /// can't see it anywhere.</summary>
    private const string SelectSql = """
        SELECT tags.id, tags.name, tags.color,
               (SELECT COUNT(*) FROM item_tags it JOIN items i ON i.id = it.item_id
                WHERE it.tag_id = tags.id AND i.deleted_at IS NULL) AS item_count
        FROM tags
        """;

    public async Task<IReadOnlyList<Tag>> ListAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SelectSql + " ORDER BY tags.name COLLATE NOCASE, tags.id;";
        var results = new List<Tag>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapTag(reader));
        }
        return results;
    }

    public async Task<Tag?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(lease.Connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = TagRules.NormalizeName(name);
        if (normalized is null) return null;
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await GetByNameCoreAsync(lease.Connection, normalized, cancellationToken).ConfigureAwait(false);
    }

    // The Core readers take the connection of a lease the caller already holds, so a public
    // method that needs a lookup in the middle of its work never acquires a second lease.
    private static async Task<Tag?> GetCoreAsync(SqliteConnection conn, long id, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE tags.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapTag(reader) : null;
    }

    private static async Task<Tag?> GetByNameCoreAsync(SqliteConnection conn, string normalizedName, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        // The column is declared COLLATE NOCASE, so '=' compares case-insensitively.
        cmd.CommandText = SelectSql + " WHERE tags.name = $name;";
        cmd.Parameters.AddWithValue("$name", normalizedName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapTag(reader) : null;
    }

    public async Task<Tag> GetOrCreateAsync(string name, string? color, CancellationToken cancellationToken)
    {
        var normalized = TagRules.NormalizeName(name)
            ?? throw new ArgumentException("Tag name is empty.", nameof(name));
        Tag tag;
        int inserted;
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var conn = lease.Connection;
            var existing = await GetByNameCoreAsync(conn, normalized, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return existing;

            await using var cmd = conn.CreateCommand();
            // OR IGNORE: if another caller created the same name before this statement ran,
            // fall through to the re-read below instead of throwing on the UNIQUE constraint.
            cmd.CommandText = "INSERT OR IGNORE INTO tags (name, color, created_at) VALUES ($name, $color, $now);";
            cmd.Parameters.AddWithValue("$name", normalized);
            cmd.Parameters.AddWithValue("$color", (object?)TagRules.NormalizeColor(color) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            inserted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            tag = await GetByNameCoreAsync(conn, normalized, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tag '{normalized}' could not be created.");
        }
        if (inserted > 0) Changed?.Invoke(this, EventArgs.Empty);
        return tag;
    }

    public async Task<bool> UpdateAsync(long id, string name, string? color, CancellationToken cancellationToken)
    {
        var normalized = TagRules.NormalizeName(name)
            ?? throw new ArgumentException("Tag name is empty.", nameof(name));
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var conn = lease.Connection;
            var current = await GetCoreAsync(conn, id, cancellationToken).ConfigureAwait(false);
            if (current is null) return false;

            var clash = await GetByNameCoreAsync(conn, normalized, cancellationToken).ConfigureAwait(false);
            if (clash is not null && clash.Id != id)
                throw new InvalidOperationException($"A tag named '{clash.Name}' already exists.");

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE tags SET name = $name, color = $color WHERE id = $id;";
                update.Parameters.AddWithValue("$name", normalized);
                update.Parameters.AddWithValue("$color", (object?)TagRules.NormalizeColor(color) ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            // Only a rename changes what search should match; a recolour leaves tag_text alone.
            if (!string.Equals(current.Name, normalized, StringComparison.Ordinal))
            {
                await using var refresh = conn.CreateCommand();
                refresh.Transaction = tx;
                refresh.CommandText = TagSql.RefreshItemsOfTag;
                refresh.Parameters.AddWithValue("$tag", id);
                await refresh.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        int rows;
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var conn = lease.Connection;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Re-index the affected items first, while item_tags still says which ones they are;
            // the DELETE below then cascades the join rows away. Same transaction so search never
            // sees a half-applied state.
            await using (var refresh = conn.CreateCommand())
            {
                refresh.Transaction = tx;
                refresh.CommandText = TagSql.RefreshItemsOfTagExcludingIt;
                refresh.Parameters.AddWithValue("$tag", id);
                await refresh.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var unlink = conn.CreateCommand())
            {
                unlink.Transaction = tx;
                // Explicit join cleanup next to the FK cascade: harmless when foreign_keys is ON,
                // and keeps the table clean on a connection opened without the pragma.
                unlink.CommandText = "DELETE FROM item_tags WHERE tag_id = $id;";
                unlink.Parameters.AddWithValue("$id", id);
                await unlink.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM tags WHERE id = $id;";
                del.Parameters.AddWithValue("$id", id);
                rows = await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (rows > 0) Changed?.Invoke(this, EventArgs.Empty);
        return rows > 0;
    }

    private static Tag MapTag(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Name: reader.GetString(1),
        Color: reader.IsDBNull(2) ? null : reader.GetString(2),
        ItemCount: reader.GetInt32(3));
}
