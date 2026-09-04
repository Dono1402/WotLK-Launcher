# Atlas avatar backend operations

These files are the reviewed production configuration for Atlas Launcher 1.3.0.
They do not publish launcher binaries or patch notes.

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
`WOTLK_LAUNCHER_MAX_SCHEMA_VERSION`. Atlas Launcher 1.3.0 requires schema `0005`,
configured through `wotlk-launcher-api-migration-ceiling.conf`. Migration `0004`
moves launcher-owned foreign keys to the Atlas profile boundary and migration
`0005` adds the optional status and bio fields.

Before starting a release candidate, the deployment script must fail unless the
effective systemd environment contains exactly
`WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=5`. It must then inspect
`atlas_launcher_schema_history` and fail if any version greater than `0005` is
already present. Missing, blank, signed, padded, whitespace-surrounded, zero or
non-numeric values are invalid and must never fall back to applying every
migration.

`deploy-launcher-api-atlas.sh` therefore requires the variable in its own
administrative process, writes it to `/etc/wotlk/launcher-api.env`, and verifies
the exact persisted line before calling `systemctl restart`. The checked-in
drop-in is the equivalent configuration for the immutable-release layout.

## Schema 0005 release validation

Before changing production, run the MySQL suites against disposable databases
and run `AtlasIdentityBoundaryMySqlTests` against a disposable copy of the
current `arthas_auth` database. The release gate verifies:

- migrations `0004` and `0005` apply once and remain idempotent;
- all launcher-owned foreign keys reference `atlas_launcher_profile`;
- existing accounts, profiles, sessions, friendships and avatars are unchanged;
- AzerothCore-only accounts remain outside the Atlas identity boundary;
- existing status and bio values remain null after the additive migration.

Take and verify an `atlas-backup` snapshot immediately before raising the
production ceiling from `0003` to `0005`.

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

## Patch notes

`PATCH-NOTES-DRAFT.md` is the local editorial source for the next release. It
contains short user-facing bullets grouped under free-form categories such as
Launcher, Jeu, PNJ, Addons, Profil, or Social.

The draft must never be copied automatically to
`/srv/wotlk/launcher-feed/patch-notes.json`. Updating that public feed is a
separate publication action performed only after Dono explicitly requests it.
