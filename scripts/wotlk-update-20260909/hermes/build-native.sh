#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

candidate_root=/opt/hermesproxy-candidates/hermes-update-20260909
candidate_sdk=/opt/hermesproxy-candidates/hermes-2e84f0b-custom-20260901/.dotnet
candidate_commit=f859d0c59696b62483a98133b1e064c15dcb5604
candidate_phase=${1:?Expected restore or build-test-publish}
case "$candidate_phase" in restore|build-test-publish) ;; *) exit 2 ;; esac
test_filter='FullyQualifiedName~AtlasLauncherWhisper|FullyQualifiedName~BnetServer|FullyQualifiedName~PetActionButtonEncoding|FullyQualifiedName~LogLevelGate|FullyQualifiedName~AddonChatCompatibility|FullyQualifiedName~DeathKnightCreationPolicy|FullyQualifiedName~ItemSparseStatWidth|FullyQualifiedName~Transport|FullyQualifiedName~ObjectUpdate'

test "$(realpath -e "$candidate_root")" = "$candidate_root"
test "$(git -C "$candidate_root/source" rev-parse HEAD)" = "$candidate_commit"
test -z "$(git -C "$candidate_root/source" status --porcelain)"
test -x "$candidate_sdk/dotnet"
mkdir -p "$candidate_root/logs" "$candidate_root/test-results" "$candidate_root/cli-home" "$candidate_root/nuget" "$candidate_root/tmp"
exec >>"$candidate_root/logs/native-build.log" 2>&1
trap 'candidate_exit=$?; printf "Native preparation exit=%s UTC=%s\n" "$candidate_exit" "$(date -u +%FT%TZ)"; exit "$candidate_exit"' EXIT

export DOTNET_ROOT="$candidate_sdk"
export PATH="$candidate_sdk:$PATH"
export DOTNET_CLI_HOME="$candidate_root/cli-home"
export NUGET_PACKAGES="$candidate_root/nuget"
export NUGET_HTTP_CACHE_PATH="$candidate_root/cli-home/nuget-http-cache"
export NUGET_PLUGINS_CACHE_PATH="$candidate_root/cli-home/nuget-plugin-cache"
export XDG_CACHE_HOME="$candidate_root/cli-home/xdg-cache"
export XDG_DATA_HOME="$candidate_root/cli-home/xdg-data"
export XDG_CONFIG_HOME="$candidate_root/cli-home/xdg-config"
export TMPDIR="$candidate_root/tmp"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_PROCESSOR_COUNT=1
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY=0
export MSBUILDDISABLENODEREUSE=1

check_disk() {
    candidate_available=$(df -B1 --output=avail "$candidate_root" | tail -n 1 | tr -d ' ')
    printf 'UTC=%s available_bytes=%s\n' "$(date -u +%FT%TZ)" "$candidate_available"
    test "$candidate_available" -ge 8589934592
}

cd "$candidate_root/source"
check_disk
dotnet --info
git show -s --format='%H%n%P%n%s' HEAD
dotnet msbuild HermesProxy/HermesProxy.csproj -getProperty:DefineConstants -getProperty:RuntimeIdentifier -p:Configuration=Release -p:RuntimeIdentifier=linux-x64

if [ "$candidate_phase" = restore ]; then
    dotnet restore HermesProxy.Tests/HermesProxy.Tests.csproj --disable-parallel --nologo -v:minimal
    check_disk
    dotnet restore HermesProxy/HermesProxy.csproj -r linux-x64 -p:UsePublishBuildSettings=true -p:PublishSingleFile=false --disable-parallel --nologo -v:minimal
    check_disk
    test -z "$(git status --porcelain)"
    printf 'NATIVE_RESTORE_COMPLETE commit=%s UTC=%s\n' "$candidate_commit" "$(date -u +%FT%TZ)"
    exit 0
fi

check_disk
dotnet build HermesProxy.Tests/HermesProxy.Tests.csproj -c Release --no-restore --nologo -m:1 -p:UseSharedCompilation=false -v:minimal
check_disk
export HERMES_TEST_MODERN_BUILD=''
dotnet test HermesProxy.Tests/HermesProxy.Tests.csproj -c Release --no-build --no-restore --nologo --logger 'trx;LogFileName=linux-invariant-full.trx' --results-directory "$candidate_root/test-results" -v:minimal
export HERMES_TEST_MODERN_BUILD=3.4.3
dotnet test HermesProxy.Tests/HermesProxy.Tests.csproj -c Release --no-build --no-restore --nologo --filter "$test_filter" --logger 'trx;LogFileName=linux-343-targeted.trx' --results-directory "$candidate_root/test-results" -v:minimal
check_disk
# Match the previous Linux deployment: self-contained, NOT single-file, with
# trimming enabled by the main project's UsePublishBuildSettings property only.
# A global PublishTrimmed=true also reaches the netstandard source generator.
dotnet publish HermesProxy/HermesProxy.csproj -c Release -r linux-x64 --self-contained true --no-restore --nologo -m:1 -p:UseSharedCompilation=false -p:UsePublishBuildSettings=true -p:PublishSingleFile=false -o "$candidate_root/publish-linux-x64" -v:minimal
check_disk
test -x "$candidate_root/publish-linux-x64/HermesProxy"
test -f "$candidate_root/publish-linux-x64/HermesProxy.runtimeconfig.json"
test -f "$candidate_root/publish-linux-x64/CSV/ItemSparse2.csv"
test -f "$candidate_root/publish-linux-x64/CSV/ItemSparse3.csv"
diff -qr "$candidate_root/source/HermesProxy/CSV" "$candidate_root/publish-linux-x64/CSV"
file "$candidate_root/publish-linux-x64/HermesProxy"
sha256sum "$candidate_root/publish-linux-x64/HermesProxy" "$candidate_root/publish-linux-x64/HermesProxy.dll" "$candidate_root/publish-linux-x64/CSV/ItemSparse2.csv" "$candidate_root/publish-linux-x64/CSV/ItemSparse3.csv"
git status --short
test -z "$(git status --porcelain)"
printf 'NATIVE_PREPARATION_COMPLETE commit=%s UTC=%s\n' "$candidate_commit" "$(date -u +%FT%TZ)"
