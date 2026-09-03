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
`Assets/Security/launcher-update-public-keys.json`. The file is intentionally
empty during 04C.1: no production private key has been created or approved.
Consequently, a production request fails closed with an unknown `keyId` until
the rollout key ceremony is complete.

Tests create ephemeral P-256 keys in memory. They are never accepted by the
production trust store; embedded production loading also rejects every key ID
using the reserved `atlas-test-` prefix.

Production proposal:

1. generate a dedicated P-256 key pair on an administrative host;
2. store the private PEM at `/etc/atlas-release-signing/launcher-update-private.pem`;
3. owner `atlas-release`, group `atlas-release`, mode `0600`, parent mode `0700`;
4. do not grant the Caddy/Kestrel users read or traverse permission;
5. do not place the private key under `/var/www`, `/srv/wotlk/launcher-releases`,
   an application checkout, a backup containing public web assets, or Git;
6. add only the public SPKI and approved `keyId` to the embedded trust resource;
7. rebuild the launcher and independently compare its embedded public key with
   the public half held by release operations.

The release script consumes the private and public paths through
`ATLAS_LAUNCHER_SIGNING_KEY` and `ATLAS_LAUNCHER_SIGNING_PUBLIC_KEY`. It never
prints either key or PEM content. `.gitignore` rejects the expected private-key
names and `.signing/` development directories.

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
manifest using OpenSSL. `source/Publish-Launcher-Atlas.sh` now requires an
explicit version and key configuration, then performs this order:

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

`scripts/release-launcher.sh` verifies the signature and versioned package again
before creating release metadata, commits, tags or remote publication.

## Production changes still required

No production change is part of 04C.1. Before rollout:

1. approve the exact HTTPS path and Caddy mapping to the existing public root;
2. configure manifest cache revalidation and immutable package caching;
3. perform the production key ceremony and secure backup policy;
4. embed the approved public SPKI/keyId and rebuild;
5. publish a signed package to the versioned path first;
6. expose the same extended manifest on the legacy HTTP endpoint;
7. smoke old launcher update, new launcher verification, UAC and rollback;
8. only then enable automatic application for the public release.
