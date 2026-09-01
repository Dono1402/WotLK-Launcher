# Local avatar storage foundation

`IAvatarStorage` isolates media persistence. Checkpoint 03A.2a provides only
`LocalAvatarStorage`; it is not registered in the production API yet.

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
checksums, and an external copy. Staging, quarantine, and trash also require a
later retention/scavenging policy before upload is enabled.

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
