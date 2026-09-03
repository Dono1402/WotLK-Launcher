# Atlas Launcher secure update channel

## Scope and current audit

Audit performed on 2026-09-03 without changing Caddy or production files.

Current deployed chain:

```text
Atlas Launcher 1.1.0
  -> GET http://152.228.225.7/launcher/launcher-update.json
  -> GET http://152.228.225.7/launcher/WotLK-Launcher.exe
       header: X-WotLK-Launcher-Update: 1
```

Observed responses:

- the HTTP manifest returns `200`, 186 bytes, from Caddy;
- the HTTP package returns `404` without the marker and `200` with it;
- `https://animeclub.fr/launcher/launcher-update.json` returns `404`;
- `https://animeclub.fr/wotlk/launcher-update.json` returns `404`;
- `https://animeclub.fr/wotlk/launcher/launcher-update.json` returns `404`.

The deployed manifest schema is currently:

```json
{
  "version": "1.1.0",
  "url": "http://152.228.225.7/launcher/WotLK-Launcher.exe",
  "size": 75643199,
  "sha256": "88295d1ea9b29f755611bc46e178154cefd0af5fb2556e6c6f77ef0d776dd7ac"
}
```

The release scripts calculate the package size and SHA-256, compare them with
the manifest, copy artifacts, then replace the public manifest. Before 04C.1,
there was no signature and the mutable top-level package could be replaced
before the manifest.

## Target chain

```text
Atlas Launcher 04C.1+
  -> GET https://animeclub.fr/wotlk/launcher/launcher-update.json
  -> verify schema, keyId and ECDSA P-256/SHA-256 signature
  -> validate immutable package URI
  -> GET https://animeclub.fr/wotlk/launcher/releases/VERSION/WotLK-Launcher.exe
  -> validate exact size and SHA-256
  -> hand the authenticated candidate to the unchanged 04B.3a transaction
```

The production HTTP client disables automatic redirects. Both manifest and
package requests reject every redirect, HTTP, non-default port, userinfo,
unexpected host, query, fragment, encoded path separator and traversal-like
path. The only host is `animeclub.fr`.

Schema-1 JSON parsing rejects unknown and duplicate properties, a UTF-8 BOM and
invalid UTF-8. Public keys are accepted only when their imported named curve is
exactly `prime256v1`/NIST P-256, not merely when they have a 256-bit size.

The manifest is limited to 64 KiB. A package is limited to 1 GiB and the
downloader stops if more bytes than the signed size are received.

## Manifest schema 1

The signed schema extends, rather than replaces, the four legacy fields:

```json
{
  "schemaVersion": 1,
  "keyId": "atlas-prod-p256-2026-01",
  "version": "1.2.0",
  "url": "https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
  "size": 12345678,
  "sha256": "lowercase-hex-sha256",
  "publishedAt": "2026-09-03T04:00:00Z",
  "signature": "base64-der-ecdsa-signature"
}
```

`signature` is an RFC 3279 DER ECDSA signature over SHA-256 of this exact UTF-8
payload, including the final LF:

```text
atlas-launcher-update-manifest-v1\n
schemaVersion=1\n
keyId={keyId}\n
version={version}\n
size={size in invariant decimal}\n
sha256={lowercase 64-character hex}\n
url={absolute package URL}\n
publishedAt={UTC yyyy-MM-ddTHH:mm:ssZ}\n
```

Every signed text field is restricted to non-empty ASCII without CR or LF.
This removes Unicode normalization and line-injection ambiguity. Property order
and JSON whitespace do not affect the signature payload.

The signature covers every value used to select and validate the release. A
change to version, size, hash, URL, timestamp or key ID invalidates it.

## Trust anchors and key handling

The launcher embeds only SubjectPublicKeyInfo public keys from
`Assets/Security/launcher-update-public-keys.json`. Checkpoint 04C.2 approved
`atlas-prod-p256-2026-01`; its public SPKI SHA-256 is
`32bb4355e1b49ec59ad757e4bb83ed231da80a2ceae986d2abca89e6fe6faa32`.

Tests create ephemeral P-256 keys in memory. They are never accepted by the
production trust store; embedded production loading also rejects every key ID
using the reserved `atlas-test-` prefix.

Production policy:

1. Atlas is the authoritative and only storage location for the private key;
2. the private PEM exists only at
   `/etc/atlas-release-signing/launcher-update-private.pem`;
3. `/etc/atlas-release-signing` is `root:root` mode `0700` and the PEM is
   `root:root` mode `0600`;
4. Caddy, `wotlklauncher`, ASP.NET and unprivileged deployment accounts have no
   read or traverse permission on this directory;
5. the private key is never copied to Git, `/var/www`, `/srv/wotlk`,
   `/opt/wotlk-launcher-api`, an application release, appsettings, a manifest,
   logs, or a separate backup;
6. only an administrative root publication process reads the private key;
7. only the public SPKI and approved `keyId` are embedded in the launcher.

No `atlas-release` account is created. The publication script requires root,
the exact private-key path and exact ownership/modes. It derives a public key
inside its private temporary directory and compares it byte-for-byte with the
embedded trust anchor before signing. Neither PEM content is printed.
`.gitignore` rejects expected private-key names and `.signing/` development
directories.

## Rotation

The trust resource supports multiple key IDs. A normal rotation is:

1. ship launcher N trusting both current key A and next key B;
2. wait until the supported population has launcher N;
3. sign later releases with B;
4. remove A in a later launcher after the overlap period.

An unknown `keyId` is rejected. If A is compromised before B is deployed, a
trusted manual release channel is required to distribute a launcher containing
B; a server-only change cannot create a new trust anchor safely.

## Legacy transition

`System.Text.Json` ignores additional JSON properties in the currently
published launcher contract. Tests deserialize a schema-1 manifest into an
explicit four-field legacy contract to preserve this fact.

During transition, serve the exact same signed manifest bytes at both:

- legacy: `http://152.228.225.7/launcher/launcher-update.json`;
- secure: `https://animeclub.fr/wotlk/launcher/launcher-update.json`.

The old launcher ignores signature fields and follows the signed HTTPS package
URL. The new launcher refuses the HTTP endpoint and verifies the signature.
The legacy HTTP endpoint can be removed after the supported installed base has
upgraded.

No unsigned fallback or user bypass exists in the new client.

## Publication

`scripts/launcher-update-manifest.py` creates and independently verifies the
manifest using OpenSSL. `source/Publish-Launcher-Atlas.sh` uses the fixed
root-only private key and requires an explicit version and `keyId`, then
performs this order:

1. build output is already final;
2. calculate final package size and SHA-256;
3. create canonical payload and sign it;
4. verify signature and package locally with the public key;
5. copy package and installer to immutable versioned public paths;
6. copy them to the private artifact store;
7. re-read and verify the public package against the signed manifest;
8. write private release metadata;
9. atomically move `launcher-update.json` last using a unique sibling file.

An existing versioned path with different bytes aborts publication. The script
does not overwrite the mutable top-level legacy binaries. Packages can receive
long-lived immutable cache headers; `launcher-update.json` must use no-cache or
short revalidation headers.

`scripts/release-launcher.sh` reconstructs the verification key from the
committed trust store and verifies the signature and versioned package again
before creating release metadata, commits, tags or remote publication. It has
no reason or permission to read the private key.

## 04C.2 production rollout

Checkpoint 04C.2 uses these public mappings:

- `/wotlk/launcher/launcher-update.json` maps to
  `/var/www/wotlk-launcher/launcher/launcher-update.json` with `no-store`;
- `/wotlk/launcher/releases/*` maps to
  `/var/www/wotlk-launcher/launcher/releases/*` with a one-year immutable cache;
- the legacy IP endpoint serves the exact same manifest bytes with `no-store`
  during migration.

The versioned package is published and verified first. The signed manifest is
then switched atomically at the HTTPS backing path and copied byte-for-byte to
the legacy endpoint. Automatic application is enabled only after HTTPS,
signature, legacy transition and updater rollback smoke tests succeed.

Rollout completed on 2026-09-03 for Atlas Launcher `1.1.1`:

- signed manifest key: `atlas-prod-p256-2026-01`;
- package SHA-256:
  `02c07636895aec453ca053e6c089322b97184f7a114ee2525829b4ce10deb0e0`;
- HTTPS and legacy manifest bytes are identical;
- HTTPS package response is immutable and the manifest response is `no-store`;
- the former top-level `1.1.0` executable remains untouched for transition;
- the Caddy configuration backup is root-only under `/etc/caddy/backups`.

The production manifest advertises the update, but installation remains an
explicit user action. A real elevation/replacement smoke test for an installed
copy under `Program Files` remains outside 04C.2 and must not be inferred from
the simulated atomic updater tests.
