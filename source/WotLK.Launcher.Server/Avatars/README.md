# Local avatar storage foundation

`IAvatarStorage` isolates media persistence. Checkpoint 03A.2b registers
`LocalAvatarStorage` through `LauncherServer:AvatarMediaRoot`; production does
not receive that build or create the target directory until checkpoint 03A.2d.

The storage root is `/srv/wotlk/atlas-media` with four children:

- `avatars`: published sets at `avatars/{avatar-id}/{version}/{size}.png`;
- `staging`: one private directory per operation;
- `quarantine`: rejected generated artifacts, never the uploaded original;
- `trash`: retired published sets awaiting later cleanup.

Publication verifies all four PNG variants, deletes the uploaded original, then
atomically moves the variants directory inside the same filesystem. A partial set
is never visible. Only 32, 64, 128, and 256 pixel square PNG files are published.

## Required host preparation (not applied in 03A.2a)

Create the storage outside `/opt`, Git, publish output, and the public Caddy root:

```sh
sudo install -d -o wotlklauncher -g wotlklauncher -m 0750 /srv/wotlk/atlas-media
sudo install -d -o wotlklauncher -g wotlklauncher -m 0750 /srv/wotlk/atlas-media/avatars
sudo install -d -o wotlklauncher -g wotlklauncher -m 0700 /srv/wotlk/atlas-media/staging
sudo install -d -o wotlklauncher -g wotlklauncher -m 0700 /srv/wotlk/atlas-media/quarantine
sudo install -d -o wotlklauncher -g wotlklauncher -m 0700 /srv/wotlk/atlas-media/trash
```

The future systemd drop-in must keep `ProtectSystem=strict` and add only:

```ini
[Service]
ReadWritePaths=/srv/wotlk/atlas-media
UMask=0027
```

Expected ownership is `wotlklauncher:wotlklauncher`; directories are never 0777.
Regular files inherit a maximum mode of 0640 through the service umask. The
current permissions under `/opt/wotlk-launcher-api` are intentionally outside
this checkpoint.

Before production, backup MySQL and `atlas-media` with a coordinated timestamp,
checksums, and an external copy.

## 03A.2b backend contract

Authenticated routes are:

- `POST /api/v1/me/avatar/photo` for a 25 MiB maximum JPEG, PNG, or static WebP;
- `DELETE /api/v1/me/avatar/photo` for an idempotent detach;
- `GET /media/avatars/{avatarId}/{version}/{size}.png` for private immutable
  media at 32, 64, 128, or 256 pixels.

Authentication alone is not sufficient: the account must own an
`atlas_launcher_profile`. AzerothCore-only accounts, including Playerbots, can
never create an asset or expose an `AvatarDescriptor`.

Uploads stream into staging, apply EXIF orientation, validate a normalized
square crop, generate fresh PNG surfaces, and publish the complete variant
directory atomically before a short database transaction changes the active
profile pointer. The original is never published and is removed on success,
validation failure, or processing failure. Invalid uploads are not quarantined.
After the database has committed, replaced or deleted media is moved to `trash`
on a best-effort basis; a later maintenance policy owns physical trash purging.

`AvatarMutationLockProvider` obtains `GET_LOCK` with a timeout of zero on a
dedicated MySQL connection. A second upload or delete for the same account is
therefore rejected immediately with `409`; closing or losing the connection
releases the lock. Avatar mutations intentionally have no frequency quota. The
legacy `atlas_launcher_avatar_upload_attempt` table remains untouched for schema
compatibility, but the runtime no longer reads from or writes to it.

`AvatarCleanupInspector` is inspection-only in 03A.2b. It reports stale staging,
abandoned Pending assets, published media not belonging to the active Ready
asset, and old Retired/Deleted assets. It never deletes anything and no timer is
registered. A later explicit maintenance command must review and execute this
plan; the currently referenced Ready asset is excluded structurally.

## 03A.2a SkiaSharp spike

SkiaSharp `4.151.1` with `SkiaSharp.NativeAssets.Linux.NoDependencies` was run
on the real Atlas Debian 13 x86-64 host under `wotlklauncher` in a transient,
network-isolated systemd unit. JPEG, PNG, WebP, EXIF orientation, crop, resize,
normalized PNG output, corrupted input rejection, excessive dimension rejection,
and a concurrency limit of two all passed.

Both NuGet packages declare the MIT license and point to the SkiaSharp source
commit `279f93f4ffa7f9fe4e9c0bc298bedc3c9e439764` on branch `release/4.151.1`.

The transient unit reported a cgroup memory peak of 126.8 MiB with no swap; the
process reported a peak working set of 156 MiB. The native library resolved only
the host's standard runtime libraries: `libstdc++`, `libpthread`, `libdl`, `libm`,
`libc`, `librt`, `libgcc_s`, and the x86-64 ELF loader. No additional image codec
package was installed on Atlas. This was an isolated spike, not a production
deployment or storage activation.
