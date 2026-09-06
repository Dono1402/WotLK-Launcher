SET SESSION TRANSACTION READ ONLY;
START TRANSACTION WITH CONSISTENT SNAPSHOT;
SELECT JSON_OBJECT(
  'observedAtUtc', UTC_TIMESTAMP(6),
  'snapshot', (
    SELECT s.snapshot
    FROM arthas_chars.atlas_armory_combat_snapshot s
    JOIN arthas_chars.characters c ON c.guid=s.guid
    WHERE c.name='Flowmage'
  )
);
ROLLBACK;
