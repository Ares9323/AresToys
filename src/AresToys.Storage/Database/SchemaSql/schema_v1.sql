-- AresToys schema v1 — single consolidated definition. Pre-release the project has no live
-- installs to migrate, so we collapse what would have been migrations 1-5 into one create.
-- The migration runner machinery is kept (schema_version + IMigration) so future changes
-- after the first public release ship as additive Migration002+ instead.

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE schema_version (
    version INTEGER NOT NULL
);
INSERT INTO schema_version (version) VALUES (1);

-- ── Items ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE items (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    kind            TEXT    NOT NULL,
    source          TEXT    NOT NULL,
    created_at      INTEGER NOT NULL,
    pinned          INTEGER NOT NULL DEFAULT 0,
    deleted_at      INTEGER,
    source_process  TEXT,
    source_window   TEXT,
    payload         BLOB    NOT NULL,
    payload_size    INTEGER NOT NULL,
    blob_ref        TEXT,
    uploaded_url    TEXT,
    uploader_id     TEXT,
    search_text     TEXT,
    -- Inline thumbnail BLOB so the popup/timeline can render previews without decrypting
    -- the full DPAPI-encrypted payload for every row.
    thumbnail       BLOB,
    -- Category bucket (CopyQ-style "tab"). Defaults to the seeded 'Clipboard' bucket so
    -- existing rows always have a home; users re-route via right-click → Move to → ….
    category        TEXT    NOT NULL DEFAULT 'Clipboard'
);

CREATE INDEX idx_items_created
    ON items(created_at DESC)
    WHERE deleted_at IS NULL;

CREATE INDEX idx_items_kind
    ON items(kind, created_at DESC)
    WHERE deleted_at IS NULL;

CREATE INDEX idx_items_pinned
    ON items(pinned, created_at DESC)
    WHERE deleted_at IS NULL AND pinned = 1;

CREATE INDEX idx_items_category
    ON items(category, created_at DESC)
    WHERE deleted_at IS NULL;

-- ── Full-text search over search_text ────────────────────────────────────────────────
CREATE VIRTUAL TABLE items_fts USING fts5(
    search_text,
    content='items',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER items_ai AFTER INSERT ON items BEGIN
    INSERT INTO items_fts(rowid, search_text) VALUES (new.id, new.search_text);
END;

CREATE TRIGGER items_ad AFTER DELETE ON items BEGIN
    INSERT INTO items_fts(items_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
END;

CREATE TRIGGER items_au AFTER UPDATE ON items BEGIN
    INSERT INTO items_fts(items_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
    INSERT INTO items_fts(rowid, search_text) VALUES (new.id, new.search_text);
END;

-- ── Categories ───────────────────────────────────────────────────────────────────────
-- The default 'Clipboard' bucket is seeded with the FontAwesome 'list' glyph (U+F03A)
-- so the popup tab strip never shows a nameless box. auto_cleanup_after is in MINUTES
-- today; the column name is generic so changing the unit semantics later doesn't need
-- a column rename.
CREATE TABLE categories (
    name               TEXT PRIMARY KEY,
    icon               TEXT,
    sort_order         INTEGER NOT NULL DEFAULT 0,
    max_items          INTEGER NOT NULL DEFAULT 0,
    auto_cleanup_after INTEGER NOT NULL DEFAULT 0
);

INSERT INTO categories (name, icon, sort_order) VALUES ('Clipboard', char(0xF03A), 0);

-- ── Pipeline profiles + settings (key/value, DPAPI-encrypted when sensitive) ─────────
CREATE TABLE pipeline_profiles (
    id              TEXT PRIMARY KEY,
    display_name    TEXT NOT NULL,
    trigger         TEXT NOT NULL,
    tasks_json      TEXT NOT NULL
);

CREATE TABLE settings (
    key             TEXT PRIMARY KEY,
    value           TEXT NOT NULL,
    is_sensitive    INTEGER NOT NULL DEFAULT 0
);

-- ── Image effects ────────────────────────────────────────────────────────────────────
-- One row per preset; the chain of effects + their parameters is a single JSON blob in
-- effects_json (produced by EffectPresetSerializer in ShareX-compatible format). Storing
-- the chain serialised avoids a per-effect row + reflection table, which would force a
-- schema bump every time we port a new effect from ShareX.
CREATE TABLE image_effect_presets (
    id            TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    effects_json  TEXT NOT NULL,
    sort_order    INTEGER NOT NULL DEFAULT 0,
    created_at    INTEGER NOT NULL,
    updated_at    INTEGER NOT NULL
);

CREATE INDEX idx_image_effect_presets_order ON image_effect_presets(sort_order);
