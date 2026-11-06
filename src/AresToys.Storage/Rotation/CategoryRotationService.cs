using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using AresToys.Storage.Blobs;
using AresToys.Storage.Database;
using AresToys.Storage.Options;

namespace AresToys.Storage.Rotation;

/// <summary>
/// Per-category soft-delete enforcement. Reads <c>categories.max_items</c> +
/// <c>categories.auto_cleanup_after</c> (interpreted as MINUTES) for every category and
/// soft-deletes the items that fall outside the cap. Pinned items always survive — caps are
/// meant for transient clutter, not user-flagged keepers.
///
/// Two entry points:
/// <list type="bullet">
///   <item><see cref="EnforceMaxItemsForAsync(string, int, System.Threading.CancellationToken)"/>
///         — fired right after <see cref="IItemStore.AddAsync"/> so a "MaxItems = 1" category
///         actually shows 1 item without waiting for the next timer tick.</item>
///   <item><see cref="RunAsync"/> — full sweep. Called by the periodic background timer + on
///         popup-open for instant feedback when the user opens the clipboard window.</item>
/// </list>
/// <see cref="PurgeAsync"/> is the only hard delete: rows soft-deleted longer ago than
/// <c>SoftDeleteGracePeriod</c> are removed for good, orphan blobs are swept and the freed
/// pages are returned to the file.
/// </summary>
public sealed class CategoryRotationService
{
    private readonly IAresToysDatabase _database;
    private readonly IBlobStore _blobs;
    private readonly StorageOptions _options;

    public CategoryRotationService(IAresToysDatabase database, IBlobStore blobs, IOptions<StorageOptions> options)
    {
        _database = database;
        _blobs = blobs;
        _options = options.Value;
    }

    /// <summary>Hard-deletes the rows soft-deleted more than <c>SoftDeleteGracePeriod</c> ago
    /// (pinned excluded), then removes the blob files no row references (outside the lease) and
    /// gives the freed pages back with an incremental vacuum. Returns the rows deleted.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoffMs = DateTimeOffset.UtcNow.Subtract(_options.Rotation.SoftDeleteGracePeriod).ToUnixTimeMilliseconds();
        int hardDeleted;
        HashSet<string> alive;
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var conn = lease.Connection;
            hardDeleted = await HardDeleteAsync(conn, cutoffMs, cancellationToken).ConfigureAwait(false);
            alive = await ReadAliveBlobRefsAsync(conn, cancellationToken).ConfigureAwait(false);
            if (hardDeleted > 0)
            {
                await using var vacuum = conn.CreateCommand();
                vacuum.CommandText = "PRAGMA incremental_vacuum;";
                await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // The blob folder sweep is file I/O and does not need the connection, so the lease is
        // already released: the clipboard pipeline is not held up while files are deleted.
        await DeleteOrphanBlobsAsync(alive, cancellationToken).ConfigureAwait(false);
        return hardDeleted;
    }

    /// <summary>Walk every category + apply both caps. Returns the total number of rows soft-
    /// deleted across all categories — useful for telemetry / debug logging, the caller can
    /// safely ignore it.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var conn = lease.Connection;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Pull every category row up-front (one query) instead of one round-trip per category.
        var caps = new List<(string Name, int MaxItems, int CleanupMinutes)>();
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT name, max_items, auto_cleanup_after FROM categories;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                caps.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        var deleted = 0;
        foreach (var (name, maxItems, cleanupMin) in caps)
        {
            if (maxItems > 0)
                deleted += await SoftDeleteOverCountAsync(conn, name, maxItems, nowMs, cancellationToken).ConfigureAwait(false);
            if (cleanupMin > 0)
                deleted += await SoftDeleteOverAgeAsync(conn, name, cleanupMin, nowMs, cancellationToken).ConfigureAwait(false);
        }
        return deleted;
    }

    /// <summary>Trim a single category to its MaxItems cap. Called from the add-item hot path so
    /// we don't pay the full sweep cost on every clipboard event. Caller passes the cap so we
    /// avoid the categories-table round-trip when MaxItems is already known (e.g. cached).</summary>
    public async Task<int> EnforceMaxItemsForAsync(string category, int maxItems, CancellationToken cancellationToken)
    {
        if (maxItems <= 0 || string.IsNullOrEmpty(category)) return 0;
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var conn = lease.Connection;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return await SoftDeleteOverCountAsync(conn, category, maxItems, nowMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Soft-delete items beyond the OFFSET <paramref name="maxItems"/> cap inside a
    /// single category. Pinned + already-deleted rows are excluded from the count and from the
    /// candidate set, so a pinned item never displaces a younger non-pinned one. Newest first
    /// (ORDER BY created_at DESC + LIMIT -1 OFFSET maxItems = "everything older than the Nth").</summary>
    private static async Task<int> SoftDeleteOverCountAsync(SqliteConnection conn, string category, int maxItems, long nowMs, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE items
            SET deleted_at = $now
            WHERE id IN (
                SELECT id FROM items
                WHERE deleted_at IS NULL AND pinned = 0 AND category = $cat
                ORDER BY created_at DESC
                LIMIT -1 OFFSET $max
            );
            """;
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$max", maxItems);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Soft-delete items in a category older than <paramref name="cleanupMinutes"/>
    /// minutes. Cap value 0 was already filtered upstream so this method is only ever called
    /// with a positive minute count.</summary>
    private static async Task<int> SoftDeleteOverAgeAsync(SqliteConnection conn, string category, int cleanupMinutes, long nowMs, CancellationToken ct)
    {
        var cutoffMs = nowMs - (long)cleanupMinutes * 60_000L;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE items
            SET deleted_at = $now
            WHERE deleted_at IS NULL AND pinned = 0 AND category = $cat AND created_at < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> HardDeleteAsync(SqliteConnection conn, long cutoffMs, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM items
            WHERE deleted_at IS NOT NULL
              AND deleted_at < $cutoff
              AND pinned = 0;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadAliveBlobRefsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var alive = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT blob_ref FROM items WHERE blob_ref IS NOT NULL;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            alive.Add(reader.GetString(0));
        }
        return alive;
    }

    private async Task<int> DeleteOrphanBlobsAsync(HashSet<string> alive, CancellationToken ct)
    {
        var removed = 0;
        await foreach (var blobRef in _blobs.EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            if (alive.Contains(blobRef)) continue;
            if (await _blobs.DeleteAsync(blobRef, ct).ConfigureAwait(false))
            {
                removed++;
            }
        }
        return removed;
    }
}
