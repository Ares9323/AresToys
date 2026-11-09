-- Schema v3 — pinned-row sort order. Explicit ordering for pinned rows so the user can
-- drag-reorder or use chevron buttons to rearrange them. Unpinned rows ignore this column
-- (their ordering is still created_at DESC tiebroken by id). New pins land at
-- pin_sort_order = MAX(existing)+1 so they queue at the bottom of the pinned strip instead
-- of jumping to the top via the created_at tiebreaker.

ALTER TABLE items ADD COLUMN pin_sort_order INTEGER NOT NULL DEFAULT 0;

INSERT INTO schema_version (version) VALUES (3);
