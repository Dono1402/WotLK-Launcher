[CmdletBinding()]
param([string]$ZigPath)

$ErrorActionPreference = 'Stop'
$atlasModuleRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ZigPath) {
    $ZigPath = Join-Path $atlasModuleRoot 'artifacts/tooling/zig-x86_64-windows-0.15.2/zig.exe'
}
if (!(Test-Path -LiteralPath $ZigPath)) {
    throw 'A portable Zig compiler is required. Supply -ZigPath; this script does not download or install tools.'
}
$atlasBuild = Join-Path $atlasModuleRoot 'artifacts/policy-tests'
New-Item -ItemType Directory -Path $atlasBuild -Force | Out-Null
$atlasPreviousGlobal = $env:ZIG_GLOBAL_CACHE_DIR
$atlasPreviousLocal = $env:ZIG_LOCAL_CACHE_DIR
try {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $atlasBuild 'cache-global'
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $atlasBuild 'cache-local'
    $atlasExe = Join-Path $atlasBuild 'atlas_chat_policy_tests.exe'
    & $ZigPath c++ -std=c++20 -O1 -Wall -Wextra -Werror -pedantic (Join-Path $PSScriptRoot 'atlas_chat_policy_tests.cpp') -o $atlasExe
    if ($LASTEXITCODE -ne 0) { throw "C++ policy test compilation failed ($LASTEXITCODE)." }
    & $atlasExe | Tee-Object -FilePath (Join-Path $atlasBuild 'result.txt')
    if ($LASTEXITCODE -ne 0) { throw "C++ policy tests failed ($LASTEXITCODE)." }
} finally {
    $env:ZIG_GLOBAL_CACHE_DIR = $atlasPreviousGlobal
    $env:ZIG_LOCAL_CACHE_DIR = $atlasPreviousLocal
}
