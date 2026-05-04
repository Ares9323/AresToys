-- Migration v2: image effect presets. One row per preset; the chain of effects + their
-- parameters is stored as a single JSON blob in effects_json (produced by
-- ShareQ.ImageEffects.Serialization.EffectPresetSerializer). Keeping the chain serialised
-- avoids a per-effect row + reflection table, which would force a schema bump every time we
-- port a new effect from ShareX.

-- IF NOT EXISTS guards keep the migration idempotent: a partial run that already created
-- the table won't fail on a second pass, and the defensive EnsureSchemaAsync in the store
-- runs the same SQL safely.
CREATE TABLE IF NOT EXISTS image_effect_presets (
    id            TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    effects_json  TEXT NOT NULL,
    sort_order    INTEGER NOT NULL DEFAULT 0,
    created_at    INTEGER NOT NULL,
    updated_at    INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_image_effect_presets_order ON image_effect_presets(sort_order);

-- INSERT OR IGNORE: schema_version has no PK, but we don't want to bump past 2 if the row
-- already exists from an earlier run.
INSERT INTO schema_version (version)
SELECT 2 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 2);
