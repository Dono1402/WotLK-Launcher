#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 LAUNCHER_EXE INSTALLER_EXE VERSION" >&2
  exit 2
fi

launcher="$(realpath "$1")"
installer="$(realpath "$2")"
version="$3"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
manifest_tool="${MANIFEST_TOOL:-$script_dir/../scripts/launcher-update-manifest.py}"
repo_root="${REPO_ROOT:-/opt/wotlk-launcher-release}"
public_root="${PUBLIC_ROOT:-/var/www/wotlk-launcher/launcher}"
artifact_root="${ARTIFACT_ROOT:-/srv/wotlk/launcher-releases}"
expected_private_key="/etc/atlas-release-signing/launcher-update-private.pem"
private_key="${ATLAS_LAUNCHER_SIGNING_KEY:-$expected_private_key}"
trust_store="${ATLAS_LAUNCHER_TRUST_STORE:-$script_dir/WotLK.Launcher/Assets/Security/launcher-update-public-keys.json}"
key_id="${ATLAS_LAUNCHER_SIGNING_KEY_ID:-}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
staging="$(mktemp -d /tmp/atlas-launcher-release.XXXXXXXX)"
manifest_next=""
public_key="$staging/launcher-update-public.pem"
trusted_public_der="$staging/trusted-public.der"
signing_public_der="$staging/signing-public.der"

chmod 0700 "$staging"

cleanup() {
  rm -rf "$staging"
  if [ -n "$manifest_next" ]; then
    rm -f "$manifest_next"
  fi
}

trap cleanup EXIT

require_file() {
  if [ ! -f "$1" ]; then
    echo "missing required file: $1" >&2
    exit 1
  fi
}

require_root_signing_key() {
  local resolved directory_state key_state
  if [ "$(id -u)" -ne 0 ]; then
    echo "Atlas launcher publication must run as root" >&2
    exit 1
  fi

  resolved="$(realpath "$1")"
  if [ "$resolved" != "$expected_private_key" ]; then
    echo "private signing key must be $expected_private_key" >&2
    exit 1
  fi

  directory_state="$(stat -c '%u:%g:%a' "$(dirname "$resolved")")"
  key_state="$(stat -c '%u:%g:%a' "$resolved")"
  if [ "$directory_state" != "0:0:700" ]; then
    echo "signing directory must be root:root mode 0700" >&2
    exit 1
  fi
  if [ "$key_state" != "0:0:600" ]; then
    echo "private signing key must be root:root mode 0600" >&2
    exit 1
  fi
}

prepare_trusted_public_key() {
  python3 - "$trust_store" "$key_id" "$trusted_public_der" <<'PY'
import base64
import json
import sys
from pathlib import Path

trust_path, key_id, output_path = sys.argv[1:]
document = json.loads(Path(trust_path).read_text(encoding="utf-8"))
matches = [entry for entry in document.get("keys", []) if entry.get("keyId") == key_id]
if len(matches) != 1:
    raise SystemExit("release keyId is not uniquely present in the embedded trust store")
try:
    encoded = matches[0]["subjectPublicKeyInfo"]
    public_key = base64.b64decode(encoded, validate=True)
except (KeyError, TypeError, ValueError):
    raise SystemExit("embedded release public key is invalid")
Path(output_path).write_bytes(public_key)
PY

  openssl pkey -pubin -inform DER -in "$trusted_public_der" -out "$public_key" >/dev/null 2>&1
  openssl pkey -in "$private_key" -pubout -outform DER -out "$signing_public_der" >/dev/null 2>&1
  if ! cmp -s "$trusted_public_der" "$signing_public_der"; then
    echo "private signing key does not match the embedded Atlas trust anchor" >&2
    exit 1
  fi
  chmod 0600 "$public_key" "$trusted_public_der" "$signing_public_der"
}

install_immutable() {
  local source="$1"
  local target="$2"
  if [ -e "$target" ]; then
    if ! cmp -s "$source" "$target"; then
      echo "immutable release already exists with different bytes: $target" >&2
      exit 1
    fi
    return
  fi
  install -m 0644 "$source" "$target"
}

require_file "$launcher"
require_file "$installer"
require_file "$manifest_tool"
require_file "$trust_store"
if [ -z "$key_id" ]; then
  echo "ATLAS_LAUNCHER_SIGNING_KEY_ID is required" >&2
  exit 1
fi
require_file "$private_key"
require_root_signing_key "$private_key"
prepare_trusted_public_key

manifest="$staging/launcher-update.json"
python3 "$manifest_tool" create \
  --package "$launcher" \
  --version "$version" \
  --key-id "$key_id" \
  --private-key "$private_key" \
  --public-key "$public_key" \
  --output "$manifest"
python3 "$manifest_tool" verify \
  --manifest "$manifest" \
  --package "$launcher" \
  --public-key "$public_key" \
  --expected-key-id "$key_id"

release_public="$public_root/releases/$version"
release_artifact="$artifact_root/v$version"
release_metadata="$repo_root/releases/v$version"
backup="$repo_root/backups/launcher-pre-v${version}-$timestamp"
install -d -m 0755 \
  "$backup" \
  "$release_public" \
  "$release_artifact" \
  "$release_metadata" \
  "$repo_root/current"

if [ -f "$public_root/launcher-update.json" ]; then
  install -m 0644 "$public_root/launcher-update.json" "$backup/launcher-update.json"
fi

# Immutable package first. The signed latest manifest is switched only after
# every candidate artifact has been copied and revalidated.
install_immutable "$launcher" "$release_public/WotLK-Launcher.exe"
install_immutable "$installer" "$release_public/WotLK-Launcher-Installer.exe"
install_immutable "$launcher" "$release_artifact/WotLK-Launcher.exe"
install_immutable "$installer" "$release_artifact/WotLK-Launcher-Installer.exe"
install -m 0644 "$manifest" "$release_artifact/launcher-update.json"

python3 "$manifest_tool" verify \
  --manifest "$manifest" \
  --package "$release_public/WotLK-Launcher.exe" \
  --public-key "$public_key" \
  --expected-key-id "$key_id"

install -m 0644 "$manifest" "$repo_root/current/launcher-update.json"
install -m 0644 "$manifest" "$release_metadata/launcher-update.json"

python3 - "$version" "$release_public/WotLK-Launcher.exe" "$release_public/WotLK-Launcher-Installer.exe" "$manifest" "$release_metadata/release.json" "$public_root" "$release_artifact" "$key_id" <<'PY'
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

version, launcher, installer, manifest, output, public_root, release, key_id = sys.argv[1:]

def asset(path):
    data = Path(path).read_bytes()
    return {
        "name": Path(path).name,
        "size": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
    }

payload = {
    "version": version,
    "tag": f"v{version}",
    "keyId": key_id,
    "createdAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "publishedRoot": public_root,
    "artifactStore": release,
    "assets": [asset(launcher), asset(installer), asset(manifest)],
}
Path(output).write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY

# This is the only mutable public release pointer and therefore the final
# publication mutation. A unique sibling keeps concurrent preparations apart.
manifest_next="$(mktemp "$public_root/.launcher-update.json.XXXXXXXX")"
install -m 0644 "$manifest" "$manifest_next"
mv "$manifest_next" "$public_root/launcher-update.json"
manifest_next=""

echo "Atlas Launcher v$version prepared and published with signed manifest keyId=$key_id"
echo "Legacy top-level binaries were not overwritten; synchronize only the signed manifest to the legacy endpoint."
