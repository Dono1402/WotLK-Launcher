# Atlas avatar backend operations

These files are the reviewed production configuration for checkpoint 03A.2d.
They do not publish the launcher V2 or change the public launcher default.

## Release layout

- immutable releases: `/opt/wotlk-launcher-api-releases/<release-id>`;
- active target: `/opt/wotlk-launcher-api` symlink;
- media: `/srv/wotlk/atlas-media`;
- service override: `/etc/systemd/system/wotlk-launcher-api.service.d/avatar-media.conf`.
- deferred migration override:
  `/etc/systemd/system/wotlk-launcher-api.service.d/migration-ceiling.conf`.

Release directories are owned by `root:wotlklauncher`, use mode `0750`, and
contain files in mode `0640` except the server executable in mode `0750`.
Deployments are written through `sudo`; the service account never writes under
`/opt`.

## Migration ceiling

Production startup requires the canonical positive integer environment variable
`WOTLK_LAUNCHER_MAX_SCHEMA_VERSION`. For the social-avatar deployment it must be
set to `3` through `wotlk-launcher-api-migration-ceiling.conf`; migration `0004`
remains embedded and immutable but is not executed.

Before starting a release candidate, the deployment script must fail unless the
effective systemd environment contains exactly
`WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=3`. It must then inspect
`atlas_launcher_schema_history` and fail if any version greater than `0003` is
already present. Missing, blank, signed, padded, whitespace-surrounded, zero or
non-numeric values are invalid and must never fall back to applying every
migration.

`deploy-launcher-api-atlas.sh` therefore requires the variable in its own
administrative process, writes it to `/etc/wotlk/launcher-api.env`, and verifies
the exact persisted line before calling `systemctl restart`. The checked-in
drop-in is the equivalent configuration for the current immutable-release
layout and is not installed by this local checkpoint.

## Schema 0003 runtime compatibility

The server diff from release `e157c6a-20260901T165326Z` through checkpoint
04C.3a was reviewed against schema version `0003`. Migration `0004` changes
foreign-key targets only; it adds no table or column consumed by the runtime.
The application-level identity boundary remains effective while the database is
capped at `0003`:

- authentication and session reads join `atlas_launcher_profile`;
- avatar mutations verify the Atlas profile before writing;
- friend lookup and listing join `atlas_launcher_profile`;
- the 03B.1 friend query joins existing `0002`/`0003` avatar tables and batches
  character lookup independently of foreign-key targets.

`MigrationCeilingTests` exercises the real 03B.1 friend/avatar query on a MySQL
8.4 schema whose seven affected foreign keys still reference `account`. It
checks an avatar descriptor, a null avatar, exclusion of an AzerothCore-only
account, and the two-query upper bound.

## Backup

Install `atlas-backup` as `/usr/local/sbin/atlas-backup` with owner `root:root`
and mode `0750`. A run creates MySQL, PostgreSQL, configuration, and
`atlas-media` artifacts under one timestamp and records all four hashes in one
manifest. The MySQL artifact already includes `arthas_auth`.

Database-first then media is deliberate. An avatar mutation racing the backup
can leave an extra object or move the formerly active object to `trash`, but it
cannot make an uncommitted upload authoritative. Restore review must preserve
`trash` until the database pointer has been reconciled.

## Rollback

1. Stop `wotlk-launcher-api.service`.
2. Atomically repoint `/opt/wotlk-launcher-api` to the preserved previous
   release.
3. Restore the previous systemd drop-in and Caddyfile only if either changed.
4. Run `systemctl daemon-reload`, start the API, then verify local and public
   health plus legacy endpoints.
5. Leave additive avatar tables in place when the previous binary ignores
   them. Restore `arthas_auth` and the matching media archive only for proven
   schema corruption or altered existing data.

Never purge `trash`, downgrade migrations, or delete an old release as part of
an emergency rollback.
