[CmdletBinding()]
param(
    [string]$CoreRoot = 'C:/Codex/Server WoTLK Custom/Arthas/core',
    [string]$ZigPath,
    [string]$BoostRoot,
    [switch]$Playerbots
)

$ErrorActionPreference = 'Stop'
$atlasModuleRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ZigPath) { $ZigPath = Join-Path $atlasModuleRoot 'artifacts/tooling/zig-x86_64-windows-0.15.2/zig.exe' }
if (!$BoostRoot) { $BoostRoot = Join-Path $atlasModuleRoot 'artifacts/tooling/boost_1_85_0' }
if (!(Test-Path -LiteralPath "$CoreRoot/src/server/game/Scripting/ScriptDefines/PlayerScript.h") -or
    !(Test-Path -LiteralPath "$BoostRoot/boost/version.hpp") -or !(Test-Path -LiteralPath $ZigPath)) {
    throw 'Existing core source, Boost headers and a portable Zig compiler are required.'
}
$atlasBuild = Join-Path $atlasModuleRoot $(if ($Playerbots) { 'artifacts/core-syntax-playerbots' } else { 'artifacts/core-syntax' })
New-Item -ItemType Directory -Path $atlasBuild -Force | Out-Null
$atlasHeaderDirectories = @(& rg --files (Join-Path $CoreRoot 'src') -g '*.h' -g '*.hpp' | ForEach-Object { Split-Path $_ -Parent } | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate existing core header directories.' }
if ($Playerbots) {
    $atlasHeaderDirectories += @(& rg --files (Join-Path $CoreRoot 'modules/mod-playerbots') -g '*.h' -g '*.hpp' |
        ForEach-Object { Split-Path $_ -Parent } | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'The requested existing mod-playerbots headers are unavailable.' }
}
$atlasIncludes = @($BoostRoot) + $atlasHeaderDirectories + @(
    "$CoreRoot/deps/fmt/include", "$CoreRoot/deps/g3dlite/include", "$CoreRoot/deps/utf8cpp",
    "$CoreRoot/deps/recastnavigation/Detour/Include", "$CoreRoot/deps/recastnavigation/Recast/Include",
    "$CoreRoot/deps/SFMT", "$CoreRoot/deps/zlib"
)
# FMT_CONSTEVAL= is the actual PUBLIC definition from deps/fmt/CMakeLists.txt.
$atlasFlags = @('-std=c++20','-O0','-c','-DBOOST_ALL_NO_LIB','-DBOOST_ASIO_NO_DEPRECATED',
    '-DBOOST_SYSTEM_USE_UTF8','-DBOOST_BIND_NO_PLACEHOLDERS','-DFMT_CONSTEVAL=')
if ($Playerbots) { $atlasFlags += '-DMOD_PLAYERBOTS' }
$atlasArguments = $atlasFlags + @($atlasIncludes | ForEach-Object { '-I"' + $_.Replace('\','/') + '"' })
$atlasPreviousGlobal = $env:ZIG_GLOBAL_CACHE_DIR
$atlasPreviousLocal = $env:ZIG_LOCAL_CACHE_DIR
try {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $atlasBuild 'cache-global'
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $atlasBuild 'cache-local'
    [System.IO.File]::WriteAllText((Join-Path $atlasBuild 'compiler-output.txt'), '')
    # Zig's driver tries to locate a non-emitted object with -fsyntax-only.
    # Compile real COFF objects instead: this also checks code generation, without linking.
    foreach ($atlasName in @('atlas_shop','atlas_shop_native','atlas_shop_loader')) {
        $atlasResponse = Join-Path $atlasBuild ($atlasName + '.rsp')
        $atlasCompile = $atlasArguments + @('"' + (Join-Path $atlasModuleRoot "src/$atlasName.cpp").Replace('\','/') + '"') +
            @('-o', ('"' + (Join-Path $atlasBuild ($atlasName + '.obj')).Replace('\','/') + '"'))
        [System.IO.File]::WriteAllLines($atlasResponse, $atlasCompile, [System.Text.UTF8Encoding]::new($false))
        & $ZigPath c++ "@$atlasResponse" 2>&1 | Tee-Object -Append -FilePath (Join-Path $atlasBuild 'compiler-output.txt')
        if ($LASTEXITCODE -ne 0) { throw "The real module/core compile check failed ($LASTEXITCODE); see compiler-output.txt." }
    }
    'PASS: actual legacy/native modules and loader compiled to COFF objects against unmodified local AzerothCore headers; no link or game execution.' |
        Tee-Object -FilePath (Join-Path $atlasBuild 'result.txt')
} finally {
    $env:ZIG_GLOBAL_CACHE_DIR = $atlasPreviousGlobal
    $env:ZIG_LOCAL_CACHE_DIR = $atlasPreviousLocal
}
