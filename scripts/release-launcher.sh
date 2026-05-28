#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
public_root="${PUBLIC_ROOT:-/var/www/wotlk-launcher/launcher}"
legacy_root="${LEGACY_ROOT:-/var/www/animeclub/launcher}"
artifact_store="${ARTIFACT_STORE:-/srv/wotlk/launcher-releases}"
server_root="${SERVER_ROOT:-/opt/wotlk-launcher-server}"
caddyfile="${CADDYFILE:-/etc/caddy/Caddyfile}"

manifest="$public_root/launcher-update.json"
launcher="$public_root/WotLK-Launcher.exe"
installer="$public_root/WotLK-Launcher-Installer.exe"

require_file() {
  if [ ! -f "$1" ]; then
    echo "Missing required file: $1" >&2
    exit 1
  fi
}

json_value() {
  node -e "const fs=require('fs'); const data=JSON.parse(fs.readFileSync(process.argv[1], 'utf8')); console.log(data[process.argv[2]] ?? '');" "$1" "$2"
}

sha256() {
  sha256sum "$1" | awk '{print $1}'
}

require_file "$manifest"
require_file "$launcher"
require_file "$installer"

version="$(json_value "$manifest" version)"
expected_size="$(json_value "$manifest" size)"
expected_sha="$(json_value "$manifest" sha256)"

if [ -z "$version" ]; then
  echo "launcher-update.json does not contain a version" >&2
  exit 1
fi

tag="v$version"
launcher_size="$(stat -c%s "$launcher")"
launcher_sha="$(sha256 "$launcher")"
installer_size="$(stat -c%s "$installer")"
installer_sha="$(sha256 "$installer")"

if [ "$launcher_size" != "$expected_size" ]; then
  echo "Launcher size mismatch: manifest=$expected_size actual=$launcher_size" >&2
  exit 1
fi

if [ "${launcher_sha,,}" != "${expected_sha,,}" ]; then
  echo "Launcher sha256 mismatch: manifest=$expected_sha actual=$launcher_sha" >&2
  exit 1
fi

if [ -d "$legacy_root" ]; then
  for name in WotLK-Launcher.exe WotLK-Launcher-Installer.exe launcher-update.json; do
    if [ -f "$legacy_root/$name" ] && ! cmp -s "$public_root/$name" "$legacy_root/$name"; then
      echo "Warning: legacy endpoint differs for $name" >&2
    fi
  done
fi

mkdir -p "$repo_root/current" "$repo_root/releases/$tag" "$repo_root/server/src" "$repo_root/caddy"
cp "$manifest" "$repo_root/current/launcher-update.json"

if [ -f "$server_root/package.json" ]; then
  cp "$server_root/package.json" "$repo_root/server/package.json"
fi
if [ -f "$server_root/README.md" ]; then
  cp "$server_root/README.md" "$repo_root/server/README.md"
fi
if [ -f "$server_root/src/server.js" ]; then
  cp "$server_root/src/server.js" "$repo_root/server/src/server.js"
fi

if [ -f "$caddyfile" ]; then
  awk '/^http:\/\/152\.228\.225\.7 \{/{flag=1} flag{print} flag && /^}/{exit}' "$caddyfile" |
    sed -E 's/Bearer [^"]+/Bearer {env.WOTLK_LAUNCHER_TOKEN}/' \
    > "$repo_root/caddy/wotlk-launcher-ip.caddy"
fi

sudo mkdir -p "$artifact_store/$tag"
sudo install -o root -g root -m 0644 "$launcher" "$artifact_store/$tag/WotLK-Launcher.exe"
sudo install -o root -g root -m 0644 "$installer" "$artifact_store/$tag/WotLK-Launcher-Installer.exe"
sudo install -o root -g root -m 0644 "$manifest" "$artifact_store/$tag/launcher-update.json"

created_at="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
cat > "$repo_root/releases/$tag/release.json" <<JSON
{
  "version": "$version",
  "tag": "$tag",
  "createdAt": "$created_at",
  "publishedRoot": "$public_root",
  "artifactStore": "$artifact_store/$tag",
  "assets": [
    {
      "name": "WotLK-Launcher.exe",
      "size": $launcher_size,
      "sha256": "$launcher_sha"
    },
    {
      "name": "WotLK-Launcher-Installer.exe",
      "size": $installer_size,
      "sha256": "$installer_sha"
    },
    {
      "name": "launcher-update.json",
      "size": $(stat -c%s "$manifest"),
      "sha256": "$(sha256 "$manifest")"
    }
  ]
}
JSON

cd "$repo_root"
if [ ! -d .git ]; then
  git init -b main
fi

git config user.name "WotLK Launcher Release Bot"
git config user.email "wotlk-launcher@atlas.local"

git add .gitignore README.md scripts/release-launcher.sh current releases server caddy

if git diff --cached --quiet; then
  echo "No git changes to commit for $tag."
else
  git commit -m "Release WotLK Launcher $tag"
fi

if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "Tag $tag already exists."
else
  git tag -a "$tag" -m "WotLK Launcher $tag"
fi

if git remote get-url origin >/dev/null 2>&1; then
  git push origin main
  git push origin "$tag"

  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    if gh release view "$tag" >/dev/null 2>&1; then
      gh release upload "$tag" "$artifact_store/$tag/"* --clobber
    else
      gh release create "$tag" "$artifact_store/$tag/"* \
        --title "WotLK Launcher $tag" \
        --notes "Release WotLK Launcher $tag"
    fi
  else
    echo "Remote pushed. GitHub release skipped: gh is not installed or not authenticated."
  fi
else
  echo "No origin remote configured. Local commit/tag created only."
fi

git --no-pager log --oneline --decorate -n 3

