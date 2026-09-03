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
source_repo_root="$(realpath "$script_dir/..")"
manifest_tool="${MANIFEST_TOOL:-$script_dir/../scripts/launcher-update-manifest.py}"
repo_root="${REPO_ROOT:-/opt/wotlk-launcher-release}"
public_root="${PUBLIC_ROOT:-/var/www/wotlk-launcher/launcher}"
artifact_root="${ARTIFACT_ROOT:-/srv/wotlk/launcher-releases}"
private_key="${ATLAS_LAUNCHER_SIGNING_KEY:-}"
public_key="${ATLAS_LAUNCHER_SIGNING_PUBLIC_KEY:-}"
key_id="${ATLAS_LAUNCHER_SIGNING_KEY_ID:-}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
staging="$(mktemp -d /tmp/atlas-launcher-release.XXXXXXXX)"
manifest_next=""

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

reject_private_key_location() {
  local resolved
  resolved="$(realpath "$1")"
  case "$resolved" in
    "$source_repo_root"/*|"$repo_root"/*|"$public_root"/*|"$artifact_root"/*)
      echo "private signing key must stay outside repository, public and artifact roots" >&2
      exit 1
      ;;
  esac
}

require_private_key_permissions() {
  local mode
  mode="$(stat -c '%a' "$1")"
  if (( (8#$mode & 077) != 0 )); then
    echo "private signing key must not grant group or world permissions (expected mode 0600 or stricter)" >&2
    exit 1
  fi
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
if [ -z "$private_key" ] || [ -z "$public_key" ] || [ -z "$key_id" ]; then
  echo "ATLAS_LAUNCHER_SIGNING_KEY, ATLAS_LAUNCHER_SIGNING_PUBLIC_KEY and ATLAS_LAUNCHER_SIGNING_KEY_ID are required" >&2
  exit 1
fi
require_file "$private_key"
require_file "$public_key"
reject_private_key_location "$private_key"
require_private_key_permissions "$private_key"

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
