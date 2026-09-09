#!/usr/bin/env bash
# Package the frozen candidate only. No binary, service or production config is run/read.
set -Eeuo pipefail
umask 077

candidate_root=/opt/hermesproxy-candidates/hermes-update-20260909
source_root="$candidate_root/source"
publish_root="$candidate_root/publish-linux-x64"
package_root="$candidate_root/package-linux-x64"
validation_root="$candidate_root/validation"
expected_commit=f859d0c59696b62483a98133b1e064c15dcb5604
upstream_commit=4247d957b78e6621047783560b3606f8fcf7ff42
archive_name=hermes-f859d0c-linux-x64-native.tar.gz

test "$(realpath -e "$candidate_root")" = "$candidate_root"
test "$(df -B1 --output=avail "$candidate_root" | tail -n1)" -ge 8589934592
test "$(git -C "$source_root" rev-parse HEAD)" = "$expected_commit"
test -z "$(git -C "$source_root" status --porcelain)"
grep -Fq "NATIVE_PREPARATION_COMPLETE commit=$expected_commit" "$candidate_root/logs/native-build.log"
test -s "$candidate_root/test-results/linux-invariant-full.trx"
test -s "$candidate_root/test-results/linux-343-targeted.trx"
git -C "$source_root" diff --exit-code "$upstream_commit" HEAD -- HermesProxy/CSV
test ! -e "$package_root"
test ! -e "$candidate_root/$archive_name"
test -x "$publish_root/HermesProxy"
test -f "$publish_root/HermesProxy.runtimeconfig.json"
test -z "$(find "$publish_root" -type l -print -quit)"
test -z "$(find "$publish_root" ! -type d ! -type f -print -quit)"

while IFS= read -r -d '' relative_path; do
    case "$relative_path" in
        CSV/*) ;;
        HermesProxy|createdump|*.dll|*.so|HermesProxy.pdb|HermesProxy.deps.json|HermesProxy.runtimeconfig.json|appsettings.json|appsettings.Development.json) ;;
        *) printf 'Unexpected publish output: %s\n' "$relative_path" >&2; exit 1 ;;
    esac
done < <(find "$publish_root" -type f -printf '%P\0')

diff -qr "$source_root/HermesProxy/CSV" "$publish_root/CSV"
cmp "$source_root/HermesProxy/appsettings.json" "$publish_root/appsettings.json"
cmp "$source_root/HermesProxy/appsettings.Development.json" "$publish_root/appsettings.Development.json"
mkdir "$package_root"
mkdir -p "$validation_root"
# Exclude the public defaults too: activation must provide an explicit configuration.
tar -C "$publish_root" --exclude='./appsettings.json' --exclude='./appsettings.Development.json' --exclude='./HermesProxy.pdb' -cf - . |
    tar -C "$package_root" -xf -
test -z "$(find "$package_root" \( -iname 'AccountData' -o -iname 'Logs' -o -iname 'certs' -o -iname '*appsettings*' -o -iname '*.pfx' -o -iname '*.pem' -o -iname '*.key' -o -iname '*.log' \) -print -quit)"
test -f "$package_root/CSV/ItemSparse2.csv"
test -f "$package_root/CSV/ItemSparse3.csv"
(
    cd "$package_root"
    find . -type f -print0 | sort -z | xargs -0 sha256sum
) > "$validation_root/files.sha256"
(
    cd "$package_root"
    sha256sum -c "$validation_root/files.sha256"
) > "$validation_root/files-verification.txt"
(
    cd "$package_root"
    find CSV -type f -print0 | sort -z | xargs -0 sha256sum
) > "$validation_root/csv.sha256"

tar --sort=name --owner=0 --group=0 --numeric-owner -C "$package_root" -czf "$candidate_root/$archive_name" .
(
    cd "$candidate_root"
    sha256sum "$archive_name" candidate.bundle build-native.sh package-native.sh validation/files.sha256 validation/csv.sha256
) > "$validation_root/artifacts.sha256"
file "$package_root/HermesProxy" > "$validation_root/native-format.txt"
tar -tzf "$candidate_root/$archive_name" > "$validation_root/archive-members.txt"
test -z "$(git -C "$source_root" status --porcelain)"
test "$(df -B1 --output=avail "$candidate_root" | tail -n1)" -ge 8589934592
printf 'Frozen source: %s\n' "$expected_commit"
printf 'Packaged files: '
find "$package_root" -type f | wc -l
printf 'Packaged CSV/data files: '
find "$package_root/CSV" -type f | wc -l
stat -c 'Archive bytes: %s' "$candidate_root/$archive_name"
sha256sum "$candidate_root/$archive_name"
