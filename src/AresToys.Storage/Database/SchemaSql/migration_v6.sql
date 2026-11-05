-- Schema v6: index for the copy-time dedup probe. ItemStore.TryDedupAsync looks for the latest
-- live row with the same kind and payload_size. Before this index, only idx_items_kind (kind,
-- created_at) existed, so the probe walked every live row of that kind as the history grew.
-- The partial WHERE matches the probe's "deleted_at IS NULL" exactly, so SQLite can use it.

CREATE INDEX IF NOT EXISTS idx_items_dedup
    ON items(kind, payload_size, created_at DESC)
    WHERE deleted_at IS NULL;

INSERT INTO schema_version (version) VALUES (6);
