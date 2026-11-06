-- Schema v7: purge the rows soft-deleted more than 24 h ago (the same grace period the rotation
-- sweep applies from now on), then switch the file to incremental auto_vacuum and VACUUM it once
-- so the space of the deleted payloads goes back to the disk. Pinned rows are kept.
-- VACUUM cannot run inside a transaction: MigrationRunner runs a migration with no transaction.

DELETE FROM items
WHERE deleted_at IS NOT NULL
  AND pinned = 0
  AND deleted_at < (CAST(strftime('%s', 'now') AS INTEGER) * 1000 - 86400000);

INSERT INTO schema_version (version) VALUES (7);

PRAGMA auto_vacuum = INCREMENTAL;
VACUUM;
