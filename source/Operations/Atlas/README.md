# Atlas avatar backend operations

These files are the reviewed production configuration for checkpoint 03A.2d.
They do not publish the launcher V2 or change the public launcher default.

## Release layout

- immutable releases: `/opt/wotlk-launcher-api-releases/<release-id>`;
- active target: `/opt/wotlk-launcher-api` symlink;
- media: `/srv/wotlk/atlas-media`;
- service override: `/etc/systemd/system/wotlk-launcher-api.service.d/avatar-media.conf`.

Release directories are owned by `root:wotlklauncher`, use mode `0750`, and
contain files in mode `0640` except the server executable in mode `0750`.
Deployments are written through `sudo`; the service account never writes under
`/opt`.

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
