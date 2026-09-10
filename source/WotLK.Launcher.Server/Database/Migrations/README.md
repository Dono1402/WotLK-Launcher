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

`0003_avatar_backend.sql` makes the active asset pointer nullable so deletion
can detach the photo atomically, and adds the persistent per-account upload
attempt ledger used by the rolling ten-minute and daily limits. Migrations
`0001` and `0002` remain byte-for-byte immutable.

`0004_atlas_profile_identity_boundary.sql` makes `atlas_launcher_profile` the
identity boundary for launcher sessions, e-mail verification, friendships and
avatar ownership. AzerothCore-only accounts remain untouched in `account`, but
cannot acquire Atlas data unless an Atlas profile already exists. Migrations
`0001`, `0002` and `0003` remain byte-for-byte immutable.

`0005_social_profile.sql` adds the optional public status and bio fields used by
the Atlas friends profile. Both fields remain empty by default and the migration
does not modify existing profile values.

`0006_private_chat.sql` adds persistent private conversations, messages, per-account
send quotas and cursors, and the game bridge inbox/outbox. It references existing
Atlas profiles and leaves account credentials, sessions and friendships unchanged.
The public API and game-inbox worker remain disabled with a schema ceiling below 6.
The production ceiling stays at 5; adding this local migration does not authorize
applying it to production.

`0007_chat_workspace.sql` adds the versioned chat workspace tables, and
`0008_global_presence.sql` adds the shared presence projection.

`0009_auth_session_families.sql` gives every launcher session an immutable
absolute refresh deadline and archives each consumed refresh-token hash. A replay
of any archived token revokes the current session in that family. Existing
sessions are backfilled with their current refresh deadline, so applying the
migration cannot extend a token that was already issued.

Runtime policy caps each active family at 4,096 archived rotations by counting
through `ix_atlas_refresh_history_session` while the session row is locked. The
4,096th archive is accepted; the next request revokes the family. Replay,
logout, targeted session revocation, password replacement, same-device login,
older-device cleanup and global session-cap cleanup delete the revoked families'
history in the same transaction. Expired history for families that simply age
out is removed opportunistically in bounded batches of 256 on later refreshes.

MySQL commits each DDL statement independently. The migrator therefore inspects
and reconciles every 0009 artifact (column, backfill, index and history table)
before recording its checksum. A restart can resume after any completed DDL
step, while an incompatible pre-existing column or index is rejected. An exact
fully built schema without its history row is validated and safely adopted.

`0010_auth_session_gc.sql` adds the composite indexes used by bounded session
garbage collection and by the per-account active/tombstone ceilings. Revoked
rows are eligible for global deletion after 60 minutes; naturally expired rows
also wait for their advertised access token to expire. Each registration,
login or password replacement drains at most 64 revoked and 64 expired parent
candidates after first draining 256 expired history rows. Each fixed parent
set has its own transaction and is locked before its history is checked by
indexed point lookup. A
parent with remaining history is skipped, so `ON DELETE CASCADE` cannot amplify
a parent batch. Every revocation is serialized by the account row before the
session and its history are locked. The runtime retains only the 64 newest
revoked rows, which bounds one account at 12 active sessions plus 64 logout
tombstones even within the time window. A pre-existing backlog needing more
than 64 repairs aborts token issuance without partial mutation; it never turns
one request transaction into an unbounded loop.

Schemas 0009 and later require Oracle MySQL 8.0 or newer. Their bounded cleanup
uses `SELECT ... FOR UPDATE SKIP LOCKED`; startup rejects MySQL 5.7 and MariaDB
before applying or serving these schema versions. The disposable concurrency
suite targets MySQL 8.4.

The 0010 reconciler validates each required index's ordered columns and ASC
direction, uniqueness, BTREE type and visibility. After an interrupted DDL or a manually prepared
partial schema it adds every missing index in one `ALTER TABLE`; an incompatible
or invisible index is rejected. A complete index set without its history row is
validated and adopted, and the final ceiling validation repeats these checks on
every startup.

Named MySQL locks are scoped from the database name. Migration commands never
run concurrently in the same schema, while separate test and production schemas
do not block each other.

Production requires `WOTLK_LAUNCHER_MAX_SCHEMA_VERSION` to be set explicitly to
a canonical positive integer. The migrator still loads and validates every
embedded migration, but applies and validates the database schema only through
that ceiling. It refuses startup when the database history is already newer than
the configured ceiling. A missing or malformed production value is rejected
before opening a database connection.

## Deployment rule

Never edit an applied migration. Add the next sequential file and test first on
a disposable copy of the current Atlas schema. Database and media backups are a
production prerequisite for the later avatar deployment checkpoint.
