-- Schema v4 — per-item Trigger (Key Sequences module). Optional short token that, when
-- the user types it in any text field, opens the Key Sequences overlay listing entries
-- bound to that token. Storage-only metadata (not surfaced on the domain Item record);
-- runtime indexing lives in the App layer's ClipboardSequenceProvider, which observes
-- IItemStore.ItemsChanged and rebuilds its dictionary off this column. Additive only —
-- existing rows default to NULL (no trigger bound). Column name is unquoted to match the
-- pipeline_profiles.trigger precedent; SQLite accepts TRIGGER as an identifier outside of
-- CREATE TRIGGER context.

ALTER TABLE items ADD COLUMN trigger TEXT NULL;

INSERT INTO schema_version (version) VALUES (4);
