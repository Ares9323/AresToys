using Microsoft.Data.Sqlite;
using AresToys.Core.Domain;
using AresToys.Storage.Database;
using AresToys.Storage.Rotation;

namespace AresToys.Storage.Items;

public sealed class ItemStore : IItemStore
{
    private readonly IAresToysDatabase _database;
    private readonly ItemSerializer _serializer;
    private readonly CategoryRotationService? _categoryRotation;

    public ItemStore(IAresToysDatabase database, ItemSerializer serializer, CategoryRotationService? categoryRotation = null)
    {
        _database = database;
        _serializer = serializer;
        _categoryRotation = categoryRotation;
    }

    public async Task<long> AddAsync(NewItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var conn = _database.GetOpenConnection();

        // Dedup: if the most recent non-deleted item has the same kind and identical payload,
        // bump its created_at instead of inserting a duplicate (e.g. user pastes "ciao" 5 times).
        var dedupId = await TryDedupAsync(conn, item, cancellationToken).ConfigureAwait(false);
        if (dedupId is not null)
        {
            ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Added, dedupId.Value));
            return dedupId.Value;
        }

        var encoded = _serializer.Encode(item.Payload);

        // Pre-generate a small PNG thumbnail for image items so the popup/timeline can render
        // previews without decrypting the full payload (the heavy DPAPI cost).
        byte[]? thumbnail = null;
        if (item.Kind == ItemKind.Image)
        {
            thumbnail = ThumbnailGenerator.TryGenerate(item.Payload, maxSide: 96);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items
                (kind, source, created_at, pinned, source_process, source_window,
                 payload, payload_size, blob_ref, uploaded_url, uploader_id, search_text, thumbnail, category, label, trigger)
            VALUES
                ($kind, $source, $created_at, $pinned, $source_process, $source_window,
                 $payload, $payload_size, $blob_ref, $uploaded_url, $uploader_id, $search_text, $thumbnail, $category, $label, $trigger);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$kind", item.Kind.ToString());
        cmd.Parameters.AddWithValue("$source", item.Source.ToString());
        cmd.Parameters.AddWithValue("$created_at", item.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$pinned", item.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$source_process", (object?)item.SourceProcess ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source_window", (object?)item.SourceWindow ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", encoded);
        cmd.Parameters.AddWithValue("$payload_size", item.PayloadSize);
        cmd.Parameters.AddWithValue("$blob_ref", (object?)item.BlobRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uploaded_url", (object?)item.UploadedUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uploader_id", (object?)item.UploaderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$search_text", (object?)item.SearchText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumbnail", (object?)thumbnail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$category", string.IsNullOrEmpty(item.Category) ? "Clipboard" : item.Category);
        cmd.Parameters.AddWithValue("$label", (object?)NormalizeLabel(item.Label) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trigger", (object?)NormalizeTrigger(item.Trigger) ?? DBNull.Value);

        var newId = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        // Per-category MaxItems enforcement happens HERE (synchronously after the insert) so
        // a "MaxItems = 1" category really only ever shows the single newest item — waiting
        // for the periodic timer would let an item sit visible for up to 30s before being
        // trimmed, which feels like a bug to the user. The cap is read from the categories
        // table inside the rotation service; we just kick it. No-op when the category has no
        // cap or when the rotation service isn't wired (legacy / test cases).
        if (_categoryRotation is not null)
        {
            try
            {
                var category = string.IsNullOrEmpty(item.Category) ? "Clipboard" : item.Category;
                var maxItems = await ReadMaxItemsAsync(conn, category, cancellationToken).ConfigureAwait(false);
                if (maxItems > 0)
                    await _categoryRotation.EnforceMaxItemsForAsync(category, maxItems, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Rotation is best-effort — a transient SQLite hiccup mustn't fail the add.
                // The next periodic sweep will catch up.
            }
        }

        ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Added, newId));
        return newId;
    }

    private static async Task<int> ReadMaxItemsAsync(SqliteConnection conn, string category, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT max_items FROM categories WHERE name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", category);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0 : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public event EventHandler<ItemsChangedEventArgs>? ItemsChanged;

    /// <summary>External hook to broadcast a refresh — used by the periodic
    /// CategoryRotationScheduler when its sweep soft-deletes rows so any open popup re-queries
    /// without having to subscribe to a separate event source.</summary>
    public void RaiseItemsChanged(ItemsChangedEventArgs args) => ItemsChanged?.Invoke(this, args);

    public async Task<ItemRecord?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return Map(reader);
    }

    public async Task<IReadOnlyList<ItemRecord>> ListAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var conn = _database.GetOpenConnection();

        // When the caller doesn't need the payload (e.g. popup list), skip its column entirely so we
        // don't pay the per-row DPAPI decryption cost — for 200 image rows that's the difference
        // between instant and several seconds.
        var payloadColumn = query.IncludePayload ? "payload" : "NULL AS payload";
        var thumbnailColumn = query.IncludeThumbnail ? "thumbnail" : "NULL AS thumbnail";
        var sql = new System.Text.StringBuilder($"SELECT items.id, items.kind, items.source, items.created_at, items.payload_size, items.pinned, items.deleted_at, items.source_process, items.source_window, items.blob_ref, items.uploaded_url, items.uploader_id, items.search_text, items.category, items.label, items.pin_sort_order, items.trigger, {payloadColumn}, {thumbnailColumn} FROM items");
        var hasFts = !string.IsNullOrWhiteSpace(query.Search);
        var hasCategory = !string.IsNullOrEmpty(query.Category);
        if (hasFts)
        {
            sql.Append(" JOIN items_fts ON items_fts.rowid = items.id");
        }

        sql.Append(" WHERE 1=1");
        if (!query.IncludeDeleted) sql.Append(" AND items.deleted_at IS NULL");
        if (query.Kind is not null) sql.Append(" AND items.kind = $kind");
        if (query.Pinned is not null) sql.Append(" AND items.pinned = $pinned");
        if (hasCategory) sql.Append(" AND items.category = $category");
        // Table-level FTS MATCH (no column prefix) searches every indexed column — currently
        // search_text + label — so a labeled row remains findable by its content snippet AND a
        // user-typed name. Schema v2 rebuilt items_fts with the label column for this.
        if (hasFts) sql.Append(" AND items_fts MATCH $search");

        // Pinned rows always float to the top. Within the pinned group rows are sorted by
        // the user-controlled pin_sort_order (low → high; new pins land at MAX+1 so they
        // queue at the bottom), then newest-first as a stable tiebreaker for pre-v2 rows
        // that all default to 0. Unpinned rows: their pin_sort_order is 0 / irrelevant, so
        // the created_at DESC tiebreaker is what actually orders them.
        sql.Append(" ORDER BY items.pinned DESC, items.pin_sort_order ASC, items.created_at DESC LIMIT $limit OFFSET $offset;");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        if (query.Kind is not null) cmd.Parameters.AddWithValue("$kind", query.Kind.Value.ToString());
        if (query.Pinned is not null) cmd.Parameters.AddWithValue("$pinned", query.Pinned.Value ? 1 : 0);
        if (hasCategory) cmd.Parameters.AddWithValue("$category", query.Category!);
        if (hasFts) cmd.Parameters.AddWithValue("$search", BuildFtsQuery(query.Search!));
        cmd.Parameters.AddWithValue("$limit", query.Limit);
        cmd.Parameters.AddWithValue("$offset", query.Offset);

        var results = new List<ItemRecord>(query.Limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public async Task<bool> SetPinnedAsync(long id, bool pinned, CancellationToken cancellationToken)
    {
        var conn = _database.GetOpenConnection();
        // Pin: stamp pin_sort_order = MAX(existing pinned) + 1 so new pins queue at the
        // bottom of the pinned strip instead of jumping to the top (which would otherwise
        // happen because every fresh pin would share order=0 and the created_at tiebreaker
        // would put the newest at the top — surprising when the user has a curated order).
        // Unpin: reset to 0 so the row doesn't keep a stale slot if the user re-pins later.
        int newOrder = 0;
        if (pinned)
        {
            await using var maxCmd = conn.CreateCommand();
            maxCmd.CommandText = "SELECT COALESCE(MAX(pin_sort_order), 0) FROM items WHERE pinned = 1 AND id <> $id AND deleted_at IS NULL;";
            maxCmd.Parameters.AddWithValue("$id", id);
            var v = await maxCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            newOrder = (v is null or DBNull ? 0 : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)) + 1;
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET pinned = $pinned, pin_sort_order = $order WHERE id = $id;";
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$order", newOrder);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var ok = rows == 1;
        if (ok) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.PinnedChanged, id));
        return ok;
    }

    /// <summary>Apply a complete pinned-order snapshot in a single transaction. The caller
    /// (UI reorder handler) computes the desired sequence of ids after a drag-drop or
    /// chevron move, then calls this with the ids in their new visual order. We renumber
    /// them 1..N atomically so concurrent reads always see a coherent ordering — partial
    /// renumbering after a power loss / connection drop would leave the strip in a weird
    /// state otherwise. Raises a single Updated event with id=-1 (broadcast) so the popup
    /// re-queries once.</summary>
    public async Task ReorderPinnedAsync(IReadOnlyList<long> orderedIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        if (orderedIds.Count == 0) return;
        var conn = _database.GetOpenConnection();
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "UPDATE items SET pin_sort_order = $order WHERE id = $id;";
            var orderParam = cmd.Parameters.Add("$order", Microsoft.Data.Sqlite.SqliteType.Integer);
            var idParam = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            for (var i = 0; i < orderedIds.Count; i++)
            {
                orderParam.Value = i + 1;       // 1-based so 0 stays reserved for "never pinned"
                idParam.Value = orderedIds[i];
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Updated, -1));
    }

    public async Task<bool> SetUploadedUrlAsync(long id, string uploaderId, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploaderId);
        ArgumentNullException.ThrowIfNull(url);
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET uploaded_url = $url, uploader_id = $uid WHERE id = $id;";
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$uid", uploaderId);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<bool> SoftDeleteAsync(long id, CancellationToken cancellationToken)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ok = await UpdateScalarAsync("UPDATE items SET deleted_at = $val WHERE id = $id AND deleted_at IS NULL;",
            id, nowMs, cancellationToken).ConfigureAwait(false);
        if (ok) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Deleted, id));
        return ok;
    }

    public async Task<bool> RestoreAsync(long id, CancellationToken cancellationToken)
    {
        var ok = await UpdateScalarAsync("UPDATE items SET deleted_at = NULL WHERE id = $id;", id, DBNull.Value, cancellationToken).ConfigureAwait(false);
        if (ok) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Restored, id));
        return ok;
    }

    public async Task<int> ClearAllExceptPinnedAsync(string? category, CancellationToken cancellationToken)
    {
        var conn = _database.GetOpenConnection();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var cmd = conn.CreateCommand();
        var sql = "UPDATE items SET deleted_at = $now WHERE deleted_at IS NULL AND pinned = 0";
        if (!string.IsNullOrEmpty(category)) sql += " AND category = $category";
        cmd.CommandText = sql + ";";
        cmd.Parameters.AddWithValue("$now", nowMs);
        if (!string.IsNullOrEmpty(category)) cmd.Parameters.AddWithValue("$category", category);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows > 0) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Deleted, -1));
        return rows;
    }

    public async Task<int> HardDeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM items
            WHERE deleted_at IS NOT NULL
              AND deleted_at < $cutoff
              AND pinned = 0;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpdatePayloadAsync(long id, ReadOnlyMemory<byte> newPayload, long newPayloadSize, CancellationToken cancellationToken)
    {
        var conn = _database.GetOpenConnection();
        var encoded = _serializer.Encode(newPayload);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET payload = $payload, payload_size = $size WHERE id = $id;";
        cmd.Parameters.AddWithValue("$payload", encoded);
        cmd.Parameters.AddWithValue("$size", newPayloadSize);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 1) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Updated, id));
        return rows == 1;
    }

    /// <summary>Turn user-typed search text into an FTS5 query: split on whitespace, drop reserved
    /// characters, append '*' so terms become prefix matches (so "col" finds "colored").</summary>
    private static string BuildFtsQuery(string raw)
    {
        var terms = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var safe = new List<string>(terms.Length);
        foreach (var term in terms)
        {
            var cleaned = new string(term.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (cleaned.Length == 0) continue;
            safe.Add(cleaned + "*");
        }
        return safe.Count == 0 ? raw : string.Join(' ', safe);
    }

    private async Task<long?> TryDedupAsync(SqliteConnection conn, NewItem item, CancellationToken cancellationToken)
    {
        // Check the latest non-deleted row with matching kind + payload_size. payload_size acts as
        // a cheap pre-filter so we only decrypt when there's a real chance of equality.
        await using var probeCmd = conn.CreateCommand();
        probeCmd.CommandText = """
            SELECT id, payload FROM items
            WHERE deleted_at IS NULL
              AND kind = $kind
              AND payload_size = $size
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        probeCmd.Parameters.AddWithValue("$kind", item.Kind.ToString());
        probeCmd.Parameters.AddWithValue("$size", item.PayloadSize);

        long candidateId;
        byte[] candidateEncrypted;
        await using (var reader = await probeCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            candidateId = reader.GetInt64(0);
            if (reader.IsDBNull(1)) return null;
            candidateEncrypted = (byte[])reader[1];
        }

        // The most recent overall must be this same row; otherwise something newer of a different
        // kind has landed in between, so it's no longer a "consecutive" duplicate.
        await using (var latestCmd = conn.CreateCommand())
        {
            latestCmd.CommandText = "SELECT id FROM items WHERE deleted_at IS NULL ORDER BY created_at DESC LIMIT 1;";
            var latestId = (long)(await latestCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (latestId != candidateId) return null;
        }

        var candidatePlain = _serializer.Decode(candidateEncrypted);
        if (!candidatePlain.AsSpan().SequenceEqual(item.Payload.Span)) return null;

        await using var bumpCmd = conn.CreateCommand();
        bumpCmd.CommandText = "UPDATE items SET created_at = $created WHERE id = $id;";
        bumpCmd.Parameters.AddWithValue("$created", item.CreatedAt.ToUnixTimeMilliseconds());
        bumpCmd.Parameters.AddWithValue("$id", candidateId);
        await bumpCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return candidateId;
    }

    private async Task<bool> UpdateScalarAsync(string sql, long id, object value, CancellationToken cancellationToken)
    {
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$val", value);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    private ItemRecord Map(SqliteDataReader reader)
    {
        var payloadOrd = reader.GetOrdinal("payload");
        ReadOnlyMemory<byte> plaintext = ReadOnlyMemory<byte>.Empty;
        if (!reader.IsDBNull(payloadOrd))
        {
            var encryptedPayload = (byte[])reader["payload"];
            plaintext = _serializer.Decode(encryptedPayload);
        }
        // Thumbnail column was added in v2 — older queries (or this same query when IncludeThumbnail
        // is false) leave it null. The column might also not exist if a test reader uses a v1 schema.
        ReadOnlyMemory<byte>? thumbnail = null;
        try
        {
            var thumbOrd = reader.GetOrdinal("thumbnail");
            if (!reader.IsDBNull(thumbOrd)) thumbnail = (byte[])reader["thumbnail"];
        }
        catch (IndexOutOfRangeException) { /* v1 schema — no thumbnail column */ }
        // Category column was added in v3 — older readers (e.g. tests on a v1/v2 schema) don't
        // have it. Fall back to the default "Clipboard" category in that case.
        string category = "Clipboard";
        try
        {
            var catOrd = reader.GetOrdinal("category");
            if (!reader.IsDBNull(catOrd)) category = reader.GetString(catOrd);
        }
        catch (IndexOutOfRangeException) { /* pre-v3 schema — column missing */ }
        // Label + pin_sort_order columns were added in schema v2 — older readers that
        // hand-craft a SELECT without them (none today, but possible in narrowly-scoped
        // diagnostic queries) fall back to null / 0 instead of throwing.
        string? label = null;
        try
        {
            var labelOrd = reader.GetOrdinal("label");
            if (!reader.IsDBNull(labelOrd)) label = reader.GetString(labelOrd);
        }
        catch (IndexOutOfRangeException) { /* column missing — label stays null */ }
        int pinSortOrder = 0;
        try
        {
            var psoOrd = reader.GetOrdinal("pin_sort_order");
            if (!reader.IsDBNull(psoOrd)) pinSortOrder = reader.GetInt32(psoOrd);
        }
        catch (IndexOutOfRangeException) { /* column missing — sort order stays 0 */ }
        // Trigger column was added in schema v4 — older readers / hand-crafted SELECTs that
        // pre-date the Key Sequences module fall back to null instead of throwing.
        string? trigger = null;
        try
        {
            var trigOrd = reader.GetOrdinal("trigger");
            if (!reader.IsDBNull(trigOrd)) trigger = reader.GetString(trigOrd);
        }
        catch (IndexOutOfRangeException) { /* pre-v4 schema — column missing */ }

        return new ItemRecord(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Kind: Enum.Parse<ItemKind>(reader.GetString(reader.GetOrdinal("kind"))),
            Source: Enum.Parse<ItemSource>(reader.GetString(reader.GetOrdinal("source"))),
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
            PayloadSize: reader.GetInt64(reader.GetOrdinal("payload_size")),
            Pinned: reader.GetInt32(reader.GetOrdinal("pinned")) == 1,
            DeletedAt: reader.IsDBNull(reader.GetOrdinal("deleted_at")) ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("deleted_at"))),
            SourceProcess: NullableString(reader, "source_process"),
            SourceWindow: NullableString(reader, "source_window"),
            BlobRef: NullableString(reader, "blob_ref"),
            UploadedUrl: NullableString(reader, "uploaded_url"),
            UploaderId: NullableString(reader, "uploader_id"),
            Payload: plaintext,
            SearchText: NullableString(reader, "search_text"),
            Thumbnail: thumbnail,
            Category: category,
            Label: label,
            PinSortOrder: pinSortOrder,
            Trigger: trigger);
    }

    public async Task<bool> SetCategoryAsync(long id, string category, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET category = $cat WHERE id = $id;";
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 1) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Updated, id));
        return rows == 1;
    }

    public async Task<bool> SetLabelAsync(long id, string? label, CancellationToken cancellationToken)
    {
        var normalised = NormalizeLabel(label);
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET label = $label WHERE id = $id;";
        cmd.Parameters.AddWithValue("$label", (object?)normalised ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 1) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Updated, id));
        return rows == 1;
    }

    public async Task<bool> SetTriggerAsync(long id, string? trigger, CancellationToken cancellationToken)
    {
        var normalised = NormalizeTrigger(trigger);
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET trigger = $trigger WHERE id = $id;";
        cmd.Parameters.AddWithValue("$trigger", (object?)normalised ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 1) ItemsChanged?.Invoke(this, new ItemsChangedEventArgs(ItemsChangeKind.Updated, id));
        return rows == 1;
    }

    public async Task<IReadOnlyList<TriggerBinding>> ListTriggerBindingsAsync(CancellationToken cancellationToken)
    {
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, trigger FROM items
            WHERE trigger IS NOT NULL AND TRIM(trigger) != '' AND deleted_at IS NULL;
            """;
        var results = new List<TriggerBinding>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new TriggerBinding(reader.GetInt64(0), reader.GetString(1)));
        }
        return results;
    }

    /// <summary>Trim and collapse empty/whitespace input to NULL. Storage doesn't enforce
    /// the [a-zA-Z0-9_]+ shape — the design spec leaves room for future modules to relax it,
    /// and validation belongs at the call site (settings UI / item editor). Done at the
    /// storage boundary so the column never gets a stray " " smuggled in from a UI bug.</summary>
    private static string? NormalizeTrigger(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim();
    }

    /// <summary>Trim, cap at 200 chars, and collapse empty/whitespace to NULL. Done at the
    /// storage boundary so callers (UI, importers, tests) all see the same canonical form
    /// and the DB never grows huge label blobs from a misbehaving consumer.</summary>
    private const int MaxLabelLength = 200;
    private static string? NormalizeLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length <= MaxLabelLength ? trimmed : trimmed[..MaxLabelLength];
    }

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }
}
