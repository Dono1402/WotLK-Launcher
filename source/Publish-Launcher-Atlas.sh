#!/usr/bin/env bash
set -euo pipefail

launcher="${1:-/tmp/wotlk-launcher-v1039/launcher/WotLK.Launcher.exe}"
installer="${2:-/tmp/wotlk-launcher-v1039/installer/WotLK.Launcher.Installer.exe}"
manifest="${3:-/tmp/v1039-launcher-update.json}"
repo_root="${REPO_ROOT:-/opt/wotlk-launcher-release}"
public_root="${PUBLIC_ROOT:-/var/www/wotlk-launcher/launcher}"
artifact_root="${ARTIFACT_ROOT:-/srv/wotlk/launcher-releases}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"

readarray -t metadata < <(python3 - "$manifest" <<'PY'
import json
import sys
from pathlib import Path

data = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
for key in ("version", "size", "sha256"):
    if key not in data:
        raise SystemExit(f"champ manifeste absent: {key}")
print(data["version"])
print(data["size"])
print(str(data["sha256"]).lower())
PY
)

version="${metadata[0]}"
expected_size="${metadata[1]}"
expected_sha="${metadata[2]}"
actual_size="$(stat -c%s "$launcher")"
actual_sha="$(sha256sum "$launcher" | awk '{print $1}')"

if [ "$actual_size" != "$expected_size" ]; then
  echo "taille launcher invalide: manifeste=$expected_size fichier=$actual_size" >&2
  exit 1
fi
if [ "${actual_sha,,}" != "${expected_sha,,}" ]; then
  echo "SHA-256 launcher invalide" >&2
  exit 1
fi

backup="$repo_root/backups/launcher-pre-v${version}-$timestamp"
release="$artifact_root/v$version"
release_metadata="$repo_root/releases/v$version"
install -d -m 0755 "$backup" "$release" "$release_metadata" "$repo_root/current"

for name in WotLK-Launcher.exe WotLK-Launcher-Installer.exe launcher-update.json; do
  if [ -f "$public_root/$name" ]; then
    install -m 0644 "$public_root/$name" "$backup/$name"
  fi
done

install -m 0644 "$launcher" "$release/WotLK-Launcher.exe"
install -m 0644 "$installer" "$release/WotLK-Launcher-Installer.exe"
install -m 0644 "$manifest" "$release/launcher-update.json"

install -m 0644 "$launcher" "$public_root/.WotLK-Launcher.exe.next"
install -m 0644 "$installer" "$public_root/.WotLK-Launcher-Installer.exe.next"
install -m 0644 "$manifest" "$public_root/.launcher-update.json.next"
mv "$public_root/.WotLK-Launcher.exe.next" "$public_root/WotLK-Launcher.exe"
mv "$public_root/.WotLK-Launcher-Installer.exe.next" "$public_root/WotLK-Launcher-Installer.exe"
mv "$public_root/.launcher-update.json.next" "$public_root/launcher-update.json"

install -m 0644 "$manifest" "$repo_root/current/launcher-update.json"
python3 - "$version" "$release/WotLK-Launcher.exe" "$release/WotLK-Launcher-Installer.exe" "$release/launcher-update.json" "$release_metadata/release.json" "$public_root" "$release" <<'PY'
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

version, launcher, installer, manifest, output, public_root, release = sys.argv[1:]

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
    "createdAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "publishedRoot": public_root,
    "artifactStore": release,
    "assets": [asset(launcher), asset(installer), asset(manifest)],
}
Path(output).write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY

echo "Launcher v$version publié: $actual_size octets, SHA-256 $actual_sha"
