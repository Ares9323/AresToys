using Microsoft.Data.Sqlite;
using ShareQ.ImageEffects;
using ShareQ.ImageEffects.Serialization;
using ShareQ.Storage.Database;

namespace ShareQ.Storage.ImageEffects;

/// <summary>SQLite-backed preset store. The chain of effects is round-tripped through
/// <see cref="EffectPresetSerializer"/> — one JSON blob per row, indexed by id.</summary>
public sealed class SqliteImageEffectPresetStore : IImageEffectPresetStore
{
    private readonly IShareQDatabase _database;
    private readonly EffectPresetSerializer _serializer;
    private bool _schemaEnsured;
    private readonly Lock _schemaGate = new();

    public SqliteImageEffectPresetStore(IShareQDatabase database, EffectPresetSerializer? serializer = null)
    {
        _database = database;
        _serializer = serializer ?? new EffectPresetSerializer();
    }

    public event EventHandler? Changed;

    /// <summary>Defensive CREATE-TABLE-IF-NOT-EXISTS run on first use. Migration002 normally
    /// creates this table at startup, but if a v1 install upgrades to a build where
    /// schema_version was bumped past 2 by other means, the migration runner skips it and the
    /// table stays missing. Running the same DDL here covers that gap idempotently.</summary>
    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        lock (_schemaGate)
        {
            if (_schemaEnsured) return;
        }

        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS image_effect_presets (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                effects_json  TEXT NOT NULL,
                sort_order    INTEGER NOT NULL DEFAULT 0,
                created_at    INTEGER NOT NULL,
                updated_at    INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_image_effect_presets_order ON image_effect_presets(sort_order);
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        lock (_schemaGate)
        {
            _schemaEnsured = true;
        }
    }

    public async Task<IReadOnlyList<EffectPreset>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, effects_json, sort_order FROM image_effect_presets ORDER BY sort_order, name;";
        var results = new List<EffectPreset>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var preset = Deserialize(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (preset is not null) results.Add(preset);
        }
        return results;
    }

    public async Task<EffectPreset?> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, effects_json FROM image_effect_presets WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return Deserialize(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task UpsertAsync(EffectPreset preset, int? sortOrder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrEmpty(preset.Id)) preset.Id = Guid.NewGuid().ToString("N");
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        // Round-trip-safe id check: name fields with embedded quotes / newlines are JSON-escaped
        // by the serializer, so storing the produced string verbatim is fine.
        var json = _serializer.Serialize(preset);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        // ON CONFLICT clause keeps the INSERT atomic and preserves created_at on update. When
        // sortOrder is null the existing value is kept (excluded.* would clobber it).
        cmd.CommandText = sortOrder is null
            ? """
              INSERT INTO image_effect_presets (id, name, effects_json, sort_order, created_at, updated_at)
              VALUES ($id, $name, $json, COALESCE((SELECT sort_order FROM image_effect_presets WHERE id = $id), 0), $now, $now)
              ON CONFLICT(id) DO UPDATE SET
                  name         = excluded.name,
                  effects_json = excluded.effects_json,
                  updated_at   = excluded.updated_at;
              """
            : """
              INSERT INTO image_effect_presets (id, name, effects_json, sort_order, created_at, updated_at)
              VALUES ($id, $name, $json, $sort, $now, $now)
              ON CONFLICT(id) DO UPDATE SET
                  name         = excluded.name,
                  effects_json = excluded.effects_json,
                  sort_order   = excluded.sort_order,
                  updated_at   = excluded.updated_at;
              """;
        cmd.Parameters.AddWithValue("$id", preset.Id);
        cmd.Parameters.AddWithValue("$name", preset.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$now", nowMs);
        if (sortOrder is not null) cmd.Parameters.AddWithValue("$sort", sortOrder.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var conn = _database.GetOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM image_effect_presets WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows > 0) Changed?.Invoke(this, EventArgs.Empty);
        return rows > 0;
    }

    public async Task ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        if (orderedIds.Count == 0) return;
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var conn = _database.GetOpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE image_effect_presets SET sort_order = $order WHERE id = $id;";
            cmd.Parameters.AddWithValue("$order", i);
            cmd.Parameters.AddWithValue("$id", orderedIds[i]);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private EffectPreset? Deserialize(string id, string name, string json)
    {
        var preset = _serializer.Deserialize(json);
        if (preset is null) return null;
        // Trust the row's id/name over whatever round-tripped through JSON — those columns are
        // the canonical source of truth (e.g. user renamed the preset and the JSON blob still
        // carries the previous name).
        preset.Id = id;
        preset.Name = name;
        return preset;
    }
}
