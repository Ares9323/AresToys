-- Schema v5: clipboard tags (issue #4). Tags are an orthogonal, many-to-many dimension next
-- to categories: one item can carry any number of tags, one tag can sit on items scattered
-- across every category. Two tables:
--   tags       one row per tag definition (unique name, case-insensitive; optional colour)
--   item_tags  the item <-> tag join, cascading on both sides so a hard-deleted item or a
--              deleted tag never leaves dangling rows behind (foreign_keys is ON for the
--              app's connection, see AresToysDatabase).
-- Search: items gains a denormalised tag_text column (space-joined tag names) that the
-- storage layer rewrites whenever an item's tags change or a tag is renamed / deleted. The
-- FTS5 index is rebuilt with it as a third column, so a free-text search matches content,
-- label and tag names together, and multi-term queries keep their AND semantics across all
-- three (a JOIN-based tag match OR-ed next to the FTS MATCH couldn't do that).

CREATE TABLE tags (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NOT NULL COLLATE NOCASE UNIQUE,
    -- '#RRGGBB' or NULL for the neutral default chip.
    color       TEXT    NULL,
    created_at  INTEGER NOT NULL
);

CREATE TABLE item_tags (
    item_id     INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    tag_id      INTEGER NOT NULL REFERENCES tags(id)  ON DELETE CASCADE,
    PRIMARY KEY (item_id, tag_id)
) WITHOUT ROWID;

-- Reverse lookup for the tag filter (items carrying tag X) and for rename / delete fan-out.
CREATE INDEX idx_item_tags_tag ON item_tags(tag_id, item_id);

ALTER TABLE items ADD COLUMN tag_text TEXT NULL;

-- Rebuild the FTS index with the new column. Same drop-and-recreate dance as migration v2:
-- external-content FTS5 tables can't gain a column in place, and the triggers reference the
-- table so they go first.
DROP TRIGGER IF EXISTS items_ai;
DROP TRIGGER IF EXISTS items_ad;
DROP TRIGGER IF EXISTS items_au;
DROP TABLE IF EXISTS items_fts;

CREATE VIRTUAL TABLE items_fts USING fts5(
    search_text,
    label,
    tag_text,
    content='items',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER items_ai AFTER INSERT ON items BEGIN
    INSERT INTO items_fts(rowid, search_text, label, tag_text) VALUES (new.id, new.search_text, new.label, new.tag_text);
END;

CREATE TRIGGER items_ad AFTER DELETE ON items BEGIN
    INSERT INTO items_fts(items_fts, rowid, search_text, label, tag_text) VALUES ('delete', old.id, old.search_text, old.label, old.tag_text);
END;

CREATE TRIGGER items_au AFTER UPDATE ON items BEGIN
    INSERT INTO items_fts(items_fts, rowid, search_text, label, tag_text) VALUES ('delete', old.id, old.search_text, old.label, old.tag_text);
    INSERT INTO items_fts(rowid, search_text, label, tag_text) VALUES (new.id, new.search_text, new.label, new.tag_text);
END;

INSERT INTO items_fts(rowid, search_text, label, tag_text)
    SELECT id, search_text, label, tag_text FROM items;

INSERT INTO schema_version (version) VALUES (5);
