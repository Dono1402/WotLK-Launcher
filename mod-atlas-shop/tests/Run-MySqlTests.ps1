[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZigPath,
    [Parameter(Mandatory)][string]$MySqlRoot,
    [Parameter(Mandatory)][string]$MySqlConfig,
    [string]$DotNet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$atlasRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$atlasMySql = [IO.Path]::GetFullPath($MySqlRoot)
$atlasConfig = [IO.Path]::GetFullPath($MySqlConfig)
if (!$atlasMySql.StartsWith($atlasRepo + '\', [StringComparison]::OrdinalIgnoreCase) -or
    !$atlasConfig.StartsWith($atlasRepo + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The disposable MySQL installation and configuration must be inside this repository.'
}
$atlasDaemon = Join-Path $atlasMySql 'bin/mysqld.exe'
$atlasClient = Join-Path $atlasMySql 'bin/mysql.exe'
$atlasAdmin = Join-Path $atlasMySql 'bin/mysqladmin.exe'
if (!(Test-Path -LiteralPath $atlasDaemon) -or !(Test-Path -LiteralPath $atlasConfig) -or !(Test-Path -LiteralPath $ZigPath)) {
    throw 'Supply existing MySQL 8.4 fixture tools/config and a Zig compiler. This script installs nothing.'
}
if (Get-NetTCPConnection -LocalPort 13307 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Port 13307 is already in use; refusing to touch the existing process.'
}
$atlasBuild = Join-Path $atlasRepo 'mod-atlas-shop/artifacts/sql-emitter'
New-Item -ItemType Directory -Path $atlasBuild -Force | Out-Null
$atlasEmitter = Join-Path $atlasBuild 'emit_delivery_sql.exe'
$atlasPreviousGlobal = $env:ZIG_GLOBAL_CACHE_DIR
$atlasPreviousLocal = $env:ZIG_LOCAL_CACHE_DIR
$atlasPreviousDb = $env:ATLAS_SHOP_TEST_DB
$atlasPreviousEmitter = $env:ATLAS_SHOP_SQL_EMITTER
$atlasStarted = $null
$atlasOwner = $null
try {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $atlasBuild 'cache-global'
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $atlasBuild 'cache-local'
    & $ZigPath c++ -std=c++20 -O1 -Wall -Wextra -Werror -pedantic (Join-Path $PSScriptRoot 'emit_delivery_sql.cpp') -o $atlasEmitter
    if ($LASTEXITCODE -ne 0) { throw 'The real module SQL emitter did not compile.' }
    & $DotNet build (Join-Path $atlasRepo 'source/WotLK.Launcher.IntegrationTests/WotLK.Launcher.IntegrationTests.csproj') -p:AtlasLocalClientBuild=true -p:NuGetAudit=false --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Launcher/API test build failed.' }
    $atlasStarted = Start-Process -FilePath $atlasDaemon -ArgumentList ('--defaults-file="' + $atlasConfig + '"') -WindowStyle Hidden -PassThru
    for ($atlasAttempt = 0; $atlasAttempt -lt 30; $atlasAttempt++) {
        $atlasListener = Get-NetTCPConnection -LocalPort 13307 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($atlasListener) { break }
        Start-Sleep -Milliseconds 500
    }
    if (!$atlasListener -or $atlasListener.LocalAddress -ne '127.0.0.1') { throw 'Expected loopback fixture unavailable.' }
    $atlasProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($atlasListener.OwningProcess)"
    if (($atlasProcess.ProcessId -ne $atlasStarted.Id -and $atlasProcess.ParentProcessId -ne $atlasStarted.Id) -or
        $atlasProcess.ExecutablePath -ne $atlasDaemon -or !$atlasProcess.CommandLine.Contains($atlasConfig)) { throw 'Unexpected listener ownership.' }
    $atlasOwner = $atlasProcess.ProcessId
    $atlasDatabase = 'atlas_shop_test_rename_' + [Guid]::NewGuid().ToString('N').Substring(0,12)
    $env:ATLAS_SHOP_TEST_DB = 'Server=127.0.0.1;Port=13307;User ID=root;Password=;Database=' + $atlasDatabase + ';Pooling=false;AllowPublicKeyRetrieval=true;SslMode=None'
    $env:ATLAS_SHOP_SQL_EMITTER = $atlasEmitter
    & $DotNet (Join-Path $atlasRepo 'source/WotLK.Launcher.IntegrationTests/bin/Debug/net8.0-windows10.0.17763.0/WotLK.Launcher.IntegrationTests.dll') --shop-rename-mysql
    if ($LASTEXITCODE -ne 0) { throw 'Shop rename integration tests failed.' }
    $atlasQuery = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME IN ('$atlasDatabase','${atlasDatabase}_chars');"
    $atlasRemaining = & $atlasClient --no-defaults --protocol=TCP --host=127.0.0.1 --port=13307 --user=root --batch --skip-column-names --execute=$atlasQuery
    if ($LASTEXITCODE -ne 0 -or $atlasRemaining -ne '0') { throw 'Disposable databases were not cleaned.' }
    'Both disposable databases removed.'
}
finally {
    $env:ZIG_GLOBAL_CACHE_DIR = $atlasPreviousGlobal
    $env:ZIG_LOCAL_CACHE_DIR = $atlasPreviousLocal
    $env:ATLAS_SHOP_TEST_DB = $atlasPreviousDb
    $env:ATLAS_SHOP_SQL_EMITTER = $atlasPreviousEmitter
    if ($atlasOwner) {
        $atlasCurrent = Get-CimInstance Win32_Process -Filter "ProcessId=$atlasOwner"
        if ($atlasCurrent.ExecutablePath -eq $atlasDaemon -and $atlasCurrent.CommandLine.Contains($atlasConfig)) {
            & $atlasAdmin --no-defaults --protocol=TCP --host=127.0.0.1 --port=13307 --user=root shutdown
            if ($LASTEXITCODE -ne 0) { throw 'Owned MySQL fixture did not shut down.' }
        }
    }
    elseif ($atlasStarted -and !$atlasStarted.HasExited) { Stop-Process -InputObject $atlasStarted }
    if ($atlasStarted) {
        if (Get-NetTCPConnection -LocalPort 13307 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 13307 is still listening.' }
        'Local MySQL fixture stopped; port 13307 closed.'
    }
}
