-- Schema v2 — per-item Label (CopyQ "Notes" equivalent). Optional free-text title that
-- replaces the auto-derived row preview in the UI. The FTS5 virtual table is recreated
-- with the label column so search matches across both content and label transparently.

ALTER TABLE items ADD COLUMN label TEXT NULL;

-- Recreate the FTS index to include the new column. External-content FTS5 tables can't
-- have columns added in-place, so we drop and rebuild. Triggers reference the table by
-- name and must be dropped first or the DROP TABLE fails with "trigger ... still in use".
DROP TRIGGER IF EXISTS items_ai;
DROP TRIGGER IF EXISTS items_ad;
DROP TRIGGER IF EXISTS items_au;
DROP TABLE IF EXISTS items_fts;

CREATE VIRTUAL TABLE items_fts USING fts5(
    search_text,
    label,
    content='items',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER items_ai AFTER INSERT ON items BEGIN
    INSERT INTO items_fts(rowid, search_text, label) VALUES (new.id, new.search_text, new.label);
END;

CREATE TRIGGER items_ad AFTER DELETE ON items BEGIN
    INSERT INTO items_fts(items_fts, rowid, search_text, label) VALUES ('delete', old.id, old.search_text, old.label);
END;

CREATE TRIGGER items_au AFTER UPDATE ON items BEGIN
    INSERT INTO items_fts(items_fts, rowid, search_text, label) VALUES ('delete', old.id, old.search_text, old.label);
    INSERT INTO items_fts(rowid, search_text, label) VALUES (new.id, new.search_text, new.label);
END;

-- Backfill the rebuilt FTS index from existing rows (label is NULL for every pre-v2 row,
-- which FTS5 treats as an empty document — fine, search just won't match those by label).
INSERT INTO items_fts(rowid, search_text, label)
    SELECT id, search_text, label FROM items;

INSERT INTO schema_version (version) VALUES (2);
