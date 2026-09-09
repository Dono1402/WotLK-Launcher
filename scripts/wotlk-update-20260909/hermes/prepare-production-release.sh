#!/usr/bin/env bash
# Prepare only a NEW immutable release. Never write a service or restart one.
set -euo pipefail
umask 077

test "$(id -u)" = 0
test "$(id -g)" = 0
candidate=/opt/hermesproxy-candidates/hermes-update-20260909
release_parent=/opt/hermesproxy-wotlk/releases
old_release=/opt/hermesproxy-wotlk/releases/atlas-chat-whispers-20260907
new_release=/opt/hermesproxy-wotlk/releases/hermes-f859d0c-20260909
archive="$candidate/hermes-f859d0c-linux-x64-native.tar.gz"
manifest="$candidate/validation/files.sha256"

test "$(readlink -f "$candidate")" = "$candidate"
test "$(readlink -f "$release_parent")" = "$release_parent"
test "$(readlink -f "$old_release")" = "$old_release"
test ! -e "$new_release"
test ! -L "$new_release"
test "$(systemctl show hermesproxy-wotlk.service -p MainPID --value)" = 1910530
test "$(readlink /proc/1910530/exe)" = "$old_release/HermesProxy"
test "$(systemctl show hermesproxy-wotlk.service -p WorkingDirectory --value)" = "$old_release"
test "$(sha256sum "$archive" | cut -d ' ' -f 1)" = 61785e2db59b0586874fe055c2e94549d2f9de5d3375b4d73fb295eab3e38d61
test "$(sha256sum "$manifest" | cut -d ' ' -f 1)" = 3431950d9704608f6b9a33638ce56eb533cc634db20cb6804806b6b2d8c7d8fa
test "$(wc -l < "$manifest")" = 233
test -z "$(find "$old_release" -maxdepth 1 -name 'appsettings.Production.json' -print -quit)"

# Shared runtime directories must match the existing release exactly.
for name in AccountData Logs PacketsLog; do
    target="/opt/hermesproxy-wotlk/$name"
    test -L "$old_release/$name"
    test "$(readlink "$old_release/$name")" = "$target"
    test -d "$target"
    test "$(readlink -f "$target")" = "$target"
done

# The archive is the previously built, smoke-tested, SHA-pinned package.
# Reject any unexpected path before extracting into the one new directory.
while IFS= read -r member; do
    case "$member" in
        /*|../*|*/../*|*/..|*\\*) printf 'Unsafe archive path\n' >&2; exit 1 ;;
    esac
done < <(tar -tzf "$archive")
install -d -m 0700 -o root -g root "$new_release"
tar --no-same-owner -xzf "$archive" -C "$new_release"
test -z "$(find "$new_release" ! -type f ! -type d -print -quit)"
test "$(find "$new_release" -type f | wc -l)" = 233
test -z "$(find "$new_release" -iname 'appsettings*.json' -print -quit)"
(cd "$new_release" && sha256sum --quiet -c "$manifest")

for name in AccountData Logs PacketsLog; do
    test ! -e "$new_release/$name"
    test ! -L "$new_release/$name"
    ln -s -- "/opt/hermesproxy-wotlk/$name" "$new_release/$name"
done

# Preserve executable-vs-data semantics while making only the new code tree immutable.
find "$new_release" -type f -perm /111 -exec chmod 0555 {} +
find "$new_release" -type f ! -perm /111 -exec chmod 0444 {} +
find "$new_release" -type d -exec chmod 0555 {} +
test -z "$(find "$new_release" -type f -perm /222 -print -quit)"
(cd "$new_release" && sha256sum --quiet -c "$manifest")

sudo -u hermesproxy test -x "$new_release/HermesProxy"
sudo -u hermesproxy test -r "$new_release/HermesProxy.dll"
sudo -u hermesproxy test ! -w "$new_release"
sudo -u hermesproxy test -r /opt/hermesproxy-wotlk/appsettings.atlas.json
sudo -u hermesproxy test -r /opt/hermesproxy-wotlk/certs/animeclub.fr.pfx
for name in AccountData Logs PacketsLog; do
    sudo -u hermesproxy test -w "$new_release/$name"
done

test "$(systemctl show hermesproxy-wotlk.service -p MainPID --value)" = 1910530
test "$(readlink /proc/1910530/exe)" = "$old_release/HermesProxy"
test "$(systemctl show hermesproxy-wotlk.service -p WorkingDirectory --value)" = "$old_release"
test ! -e /etc/systemd/system/hermesproxy-wotlk.service.d/90-hermes-update-20260909.conf
printf 'NEW_RELEASE_PREPARED=%s\n' "$new_release"
printf 'PACKAGE_FILES_VERIFIED=233\nSHARED_RUNTIME_LINKS=AccountData,Logs,PacketsLog\n'
printf 'SERVICE_CONFIGURATION_CHANGED=false\nSERVICE_RESTARTED=false\n'
sha256sum "$new_release/HermesProxy.dll"
stat -c '%A %U:%G %n' "$new_release" "$new_release/HermesProxy" "$new_release/HermesProxy.dll"
