# Atlas Launcher SQL migrations

The API applies embedded SQL migrations before serving requests. Every migration
is named `NNNN_name.sql`, starts at `0001`, and is immutable once recorded.

`atlas_launcher_schema_history` records the version, name, SHA-256, UTC apply
date, duration, and application version. Startup rejects unknown versions,
changed names, changed checksums, gaps, and structural schema drift.

## Baseline

`0001_legacy_baseline.sql` describes the four launcher tables already present on
Atlas. On an existing database, the migrator does not execute this file blindly:
it inspects the real tables, columns, order, defaults, indexes, foreign keys,
engine, and collation. Only an exact match is adopted as version 1. A partial or
different legacy schema stops startup.

On an empty compatible database, the same file creates the legacy tables. The
AzerothCore `account` table must already exist because the launcher tables refer
to it.

## Avatar schema

`0002_profile_avatar.sql` creates:

- `atlas_launcher_avatar_asset`: UUID, owner, monotone owner version, status,
  storage key, and timestamps;
- `atlas_launcher_avatar_variant`: size, content type, byte length, and SHA-256;
- `atlas_launcher_profile_avatar`: the single active avatar pointer per profile.

Status values reserved by the schema are `0 Pending`, `1 Ready`, `2 Retired`,
and `3 Deleted`. The legacy `atlas_launcher_profile.avatar_key` column remains
unchanged for older launchers.

Named MySQL locks are scoped from the database name. Migration commands never
run concurrently in the same schema, while separate test and production schemas
do not block each other.

## Deployment rule

Never edit an applied migration. Add the next sequential file and test first on
a disposable copy of the current Atlas schema. Database and media backups are a
production prerequisite for the later avatar deployment checkpoint.
