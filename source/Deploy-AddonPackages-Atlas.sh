#!/usr/bin/env bash
set -euo pipefail

input_root="${1:-/tmp/atlas-addon-release}"
public_root="${PUBLIC_ROOT:-/var/www/wotlk-launcher/launcher/addons}"
source_root="${SOURCE_ROOT:-/opt/wotlk-launcher-release/source/artifacts/addons}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_root="${BACKUP_ROOT:-/opt/wotlk-launcher-release/backups/addons-$timestamp}"

python3 - "$input_root" <<'PY'
import hashlib
import json
import sys
from pathlib import Path
from urllib.parse import unquote, urlparse

root = Path(sys.argv[1])
catalog_path = root / "catalog.json"
packages_root = root / "packages"

catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
if catalog.get("schemaVersion") != 1 or not isinstance(catalog.get("addons"), list):
    raise SystemExit("catalogue addons invalide")

hosted = []
for addon in catalog["addons"]:
    for package in [addon, *addon.get("components", [])]:
        parsed = urlparse(package["url"])
        if parsed.hostname != "animeclub.fr" or not parsed.path.startswith("/wotlk/addons/packages/"):
            continue

        archive = packages_root / Path(unquote(parsed.path)).name
        if not archive.is_file():
            raise SystemExit(f"archive Atlas absente: {archive.name}")
        if archive.stat().st_size != int(package["size"]):
            raise SystemExit(f"taille invalide: {archive.name}")

        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        if digest.lower() != str(package["sha256"]).lower():
            raise SystemExit(f"SHA-256 invalide: {archive.name}")
        hosted.append(archive.name)

if not hosted:
    raise SystemExit("aucune archive Atlas référencée par le catalogue")

print("Archives Atlas validées:", ", ".join(sorted(hosted)))
PY

publish_tree() {
  local destination="$1"
  local backup_name="$2"
  local parent
  local name
  local stage
  local previous

  parent="$(dirname "$destination")"
  name="$(basename "$destination")"
  stage="$(mktemp -d "$parent/.${name}-stage-XXXXXX")"
  previous="$parent/.${name}-previous-$timestamp"

  chmod 0755 "$stage"
  install -d -m 0755 "$stage/packages"
  install -m 0644 "$input_root/catalog.json" "$stage/catalog.json"
  if [ -f "$input_root/SOURCES.json" ]; then
    install -m 0644 "$input_root/SOURCES.json" "$stage/SOURCES.json"
  fi
  find "$input_root/packages" -maxdepth 1 -type f -name '*.zip' -exec install -m 0644 -t "$stage/packages" {} +

  if [ -e "$destination" ]; then
    mv "$destination" "$previous"
  fi
  mv "$stage" "$destination"
  if [ -e "$previous" ]; then
    mv "$previous" "$backup_root/$backup_name"
  fi
}

install -d -m 0755 "$backup_root"
publish_tree "$public_root" public
publish_tree "$source_root" source

echo "Catalogue et archives addons publiés le $timestamp."
