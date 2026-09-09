#!/usr/bin/env bash
# Explicitly reviewed production operation: Hermes only, never worldserver.
set -eEuo pipefail
umask 077
test "$(id -u)" = 0

unit=hermesproxy-wotlk.service
candidate=/opt/hermesproxy-candidates/hermes-update-20260909
old=/opt/hermesproxy-wotlk/releases/atlas-chat-whispers-20260907
new=/opt/hermesproxy-wotlk/releases/hermes-f859d0c-20260909
backup=/opt/hermesproxy-wotlk/rollback-hermes-f859d0c-20260909
dropin=/etc/systemd/system/hermesproxy-wotlk.service.d/90-hermes-update-20260909.conf
pending="$dropin.pending"
template="$candidate/90-hermes-update-20260909.conf"
manifest="$candidate/validation/files.sha256"
cutover_started=0

wait_identity_and_ports() {
    local wanted=$1 pid ready attempt port
    for ((attempt=0; attempt<300; attempt++)); do
        pid=$(systemctl show "$unit" -p MainPID --value)
        if [[ "$pid" =~ ^[1-9][0-9]*$ ]] && [[ "$(readlink "/proc/$pid/exe" 2>/dev/null || true)" = "$wanted/HermesProxy" ]]; then
            ready=1
            for port in 1119 8081 8084 8086 8099; do
                if ! ss -H -ltnp "sport = :$port" | grep -F "pid=$pid," > /dev/null; then ready=0; fi
            done
            if test "$ready" = 1; then
                test "$(systemctl show "$unit" -p ActiveState --value)" = active || return 1
                test "$(systemctl show "$unit" -p NRestarts --value)" = 0 || return 1
                printf 'ACTIVE_HERMES_PID=%s\nACTIVE_HERMES_RELEASE=%s\n' "$pid" "$wanted"
                return 0
            fi
        fi
        sleep 0.1
    done
    printf 'Expected Hermes identity/listeners not ready within 30 seconds\n' >&2
    return 1
}

rollback_once() {
    test -d "$backup" || return 1
    test "$(sha256sum "$template" | cut -d ' ' -f 1)" = 121cff857612430e89a325da96ffdf917a37d2890458bbf59006f30e762738af || return 1
    if test -e "$dropin"; then
        cmp -s "$dropin" "$template" || { printf 'Refusing to move an unexpected drop-in\n' >&2; return 1; }
    fi
    systemctl stop "$unit" || return 1
    if test -e "$dropin"; then
        test ! -e "$backup/90-hermes-update-20260909.disabled" || return 1
        mv -- "$dropin" "$backup/90-hermes-update-20260909.disabled" || return 1
    fi
    systemctl daemon-reload || return 1
    systemctl start "$unit" || return 1
    wait_identity_and_ports "$old" || return 1
    printf 'ROLLBACK_OLD_RELEASE_ACTIVE=true\nACCOUNT_DATA_AUTOMATICALLY_RESTORED=false\n'
}

on_error() {
    local original_status=$?
    trap - ERR
    if test "$cutover_started" = 1; then
        printf 'ACTIVATION_FAILED: attempting the single reviewed rollback\n' >&2
        rollback_once || printf 'ROLLBACK_INCOMPLETE: immediate operator action required\n' >&2
    fi
    exit "$original_status"
}

case "${1:-}" in
    --rollback-reviewed)
        test "$#" = 1
        rollback_once
        exit 0
        ;;
    --activate-reviewed) test "$#" = 1 ;;
    *) printf 'Use only --activate-reviewed or --rollback-reviewed after review\n' >&2; exit 2 ;;
esac
trap on_error ERR

test "$(readlink -f /opt/hermesproxy-wotlk)" = /opt/hermesproxy-wotlk
test "$(readlink -f "$new")" = "$new"
test "$(readlink -f "$old")" = "$old"
test ! -e "$backup"
test ! -L "$backup"
test ! -e "$dropin"
test ! -L "$dropin"
test ! -e "$pending"
test ! -L "$pending"
test "$(sha256sum "$template" | cut -d ' ' -f 1)" = 121cff857612430e89a325da96ffdf917a37d2890458bbf59006f30e762738af
test "$(sha256sum "$manifest" | cut -d ' ' -f 1)" = 3431950d9704608f6b9a33638ce56eb533cc634db20cb6804806b6b2d8c7d8fa
test "$(systemctl show "$unit" -p MainPID --value)" = 1910530
test "$(readlink /proc/1910530/exe)" = "$old/HermesProxy"
test "$(systemctl show "$unit" -p WorkingDirectory --value)" = "$old"
test "$(systemctl show "$unit" -p NRestarts --value)" = 0
test "$(systemctl show arthas-worldserver.service -p MainPID --value)" = 1910234
test "$(systemctl show "$unit" -p DropInPaths --value)" = '/etc/systemd/system/hermesproxy-wotlk.service.d/20-quest-sync.conf /etc/systemd/system/hermesproxy-wotlk.service.d/30-atlas-release.conf /etc/systemd/system/hermesproxy-wotlk.service.d/60-atlas-chat-whispers.conf'
(cd "$new" && sha256sum --quiet -c "$manifest")
test "$(find "$new" -type f | wc -l)" = 233
test "$(find "$new" -type l | wc -l)" = 3
for name in AccountData Logs PacketsLog; do
    test "$(readlink "$new/$name")" = "/opt/hermesproxy-wotlk/$name"
    test "$(readlink "$old/$name")" = "/opt/hermesproxy-wotlk/$name"
done

# Compare protected env-file values with the live process without ever emitting them.
python3 - <<'PY'
import shlex
from pathlib import Path
live = dict(item.decode().split('=', 1) for item in Path('/proc/1910530/environ').read_bytes().split(b'\0') if b'=' in item)
count = 0
for line in Path('/etc/hermesproxy-wotlk.env').read_text().splitlines():
    if not line.strip() or line.lstrip().startswith(('#', ';')):
        continue
    key, value = line.split('=', 1)
    parsed = shlex.split(value, comments=False, posix=True)
    assert len(parsed) == 1 and live.get(key.strip()) == parsed[0], 'Environment drift: abort before stop'
    count += 1
assert count == 4, 'Unexpected environment-file schema: abort before stop'
PY

files=(
    /etc/systemd/system/hermesproxy-wotlk.service
    /etc/systemd/system/hermesproxy-wotlk.service.d/20-quest-sync.conf
    /etc/systemd/system/hermesproxy-wotlk.service.d/30-atlas-release.conf
    /etc/systemd/system/hermesproxy-wotlk.service.d/60-atlas-chat-whispers.conf
    /etc/systemd/system/atlas-hermes-quest-sync.service
    /usr/local/sbin/atlas-sync-hermes-quests
    /etc/hermesproxy-wotlk.env
    /opt/hermesproxy-wotlk/appsettings.atlas.json
    /opt/hermesproxy-wotlk/certs/animeclub.fr.pfx
)
for file in "${files[@]}"; do test -f "$file"; test ! -L "$file"; done
install -d -m 0700 -o root -g root "$backup" "$backup/static"
cp --parents --preserve=all -- "${files[@]}" "$backup/static/"
sha256sum "${files[@]}" > "$backup/static.sha256"
for file in "${files[@]}"; do cmp -s "$file" "$backup/static$file"; done
sha256sum --quiet -c "$backup/static.sha256"
printf 'PRIVATE_STATIC_BACKUP_VERIFIED=true\nBACKUP_PATH=%s\n' "$backup"

# Planned interruption begins. Freeze shared state after Hermes stops, before quest-sync.
cutover_started=1
systemctl stop "$unit"
test "$(systemctl show "$unit" -p MainPID --value)" = 0
cp -a -- /opt/hermesproxy-wotlk/AccountData "$backup/AccountData"
diff -qr -- /opt/hermesproxy-wotlk/AccountData "$backup/AccountData" > "$backup/accountdata-verification.log"
printf 'STOPPED_ACCOUNT_DATA_BACKUP_VERIFIED=true\n'
sha256sum --quiet -c "$backup/static.sha256"
install -m 0644 -o root -g root "$template" "$pending"
cmp -s "$pending" "$template"
mv -- "$pending" "$dropin"
systemctl daemon-reload
systemctl start "$unit"
wait_identity_and_ports "$new"
sha256sum --quiet -c "$backup/static.sha256"
(cd "$new" && sha256sum --quiet -c "$manifest")
test "$(systemctl show arthas-worldserver.service -p MainPID --value)" = 1910234
cutover_started=0
trap - ERR
printf 'HERMES_SWITCHED=true\nWORLD_RESTARTED=false\nREADINESS_TLS_BNET_CHECKS_STILL_REQUIRED=true\n'
