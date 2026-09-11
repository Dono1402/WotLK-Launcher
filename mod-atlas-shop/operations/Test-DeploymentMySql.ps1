[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MySqlRoot,
    [Parameter(Mandatory)][string]$MySqlConfig,
    [Parameter(Mandatory)][string]$DotNet,
    [ValidateSet('auth-session','migration-ceiling','account-services','legacy-rename')][string[]]$Suites = @('auth-session','migration-ceiling','account-services'),
    [string]$SqlEmitter
)

$ErrorActionPreference = 'Stop'
$atlasRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$atlasTools = [IO.Path]::GetFullPath($MySqlRoot)
$atlasConfig = [IO.Path]::GetFullPath($MySqlConfig)
foreach ($atlasPath in @($atlasTools, $atlasConfig)) {
    if (!$atlasPath.StartsWith($atlasRepo + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use only the existing disposable MySQL installation and configuration inside this repository.'
    }
}
$atlasDaemon = Join-Path $atlasTools 'bin/mysqld.exe'
$atlasClient = Join-Path $atlasTools 'bin/mysql.exe'
$atlasAdmin = Join-Path $atlasTools 'bin/mysqladmin.exe'
$atlasTests = Join-Path $atlasRepo 'source/WotLK.Launcher.IntegrationTests/bin/Debug/net8.0-windows10.0.17763.0/WotLK.Launcher.IntegrationTests.dll'
foreach ($atlasPath in @($atlasDaemon, $atlasClient, $atlasAdmin, $atlasConfig, $atlasTests, $DotNet)) {
    if (!(Test-Path -LiteralPath $atlasPath -PathType Leaf)) { throw 'Missing prerequisite: ' + $atlasPath }
}
if ('legacy-rename' -in $Suites) {
    if (!$SqlEmitter) { throw 'The legacy suite requires the compiled module SQL emitter.' }
    $SqlEmitter = [IO.Path]::GetFullPath($SqlEmitter)
    if (!$SqlEmitter.StartsWith($atlasRepo + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $SqlEmitter -PathType Leaf)) {
        throw 'Use a compiled SQL emitter inside this repository.'
    }
}
if (Get-NetTCPConnection -LocalPort 13307 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Port 13307 is already occupied; the existing process will not be touched.'
}
$atlasOutputs = Join-Path $atlasRepo 'artifacts/shop-deployment'
New-Item -ItemType Directory -Path $atlasOutputs -Force | Out-Null
$atlasStarted = $null
$atlasOwner = $null
$atlasCreated = [Collections.Generic.List[string]]::new()
$atlasSavedAuth = $env:ATLAS_AUTH_SESSION_TEST_DB
$atlasSavedCeiling = $env:ATLAS_MIGRATION_CEILING_TEST_DB
$atlasSavedShop = $env:ATLAS_SHOP_TEST_DB
$atlasSavedEmitter = $env:ATLAS_SHOP_SQL_EMITTER

function Invoke-FixtureSql([string]$Statement) {
    $atlasResult = & $atlasClient --no-defaults --protocol=TCP --host=127.0.0.1 --port=13307 --user=root --batch --skip-column-names --execute=$Statement
    if ($LASTEXITCODE -ne 0) { throw 'Disposable MySQL statement failed.' }
    return $atlasResult
}

try {
    $atlasStarted = Start-Process -FilePath $atlasDaemon -ArgumentList ('--defaults-file="' + $atlasConfig + '"') -WindowStyle Hidden -PassThru
    for ($atlasAttempt = 0; $atlasAttempt -lt 60; $atlasAttempt++) {
        $atlasListener = Get-NetTCPConnection -LocalPort 13307 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($atlasListener) { break }
        Start-Sleep -Milliseconds 500
    }
    if (!$atlasListener -or $atlasListener.LocalAddress -ne '127.0.0.1') { throw 'The loopback fixture is unavailable.' }
    $atlasProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($atlasListener.OwningProcess)"
    if (($atlasProcess.ProcessId -ne $atlasStarted.Id -and $atlasProcess.ParentProcessId -ne $atlasStarted.Id) -or
        $atlasProcess.ExecutablePath -ne $atlasDaemon -or !$atlasProcess.CommandLine.Contains($atlasConfig)) {
        throw 'The listener does not belong to this fixture.'
    }
    $atlasOwner = $atlasProcess.ProcessId
    if (!(Invoke-FixtureSql 'SELECT VERSION();').StartsWith('8.4.')) { throw 'MySQL 8.4 is required.' }
    $atlasSuffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $atlasRuns = @(
        @{ Name = 'auth-session'; Flag = '--auth-session-mysql'; Variable = 'ATLAS_AUTH_SESSION_TEST_DB'; Database = 'atlas_auth_session_test_shop_' + $atlasSuffix },
        @{ Name = 'migration-ceiling'; Flag = '--migration-ceiling-mysql'; Variable = 'ATLAS_MIGRATION_CEILING_TEST_DB'; Database = 'atlas_migration_ceiling_test_shop_' + $atlasSuffix },
        @{ Name = 'account-services'; Flag = '--shop-account-services-mysql'; Variable = 'ATLAS_SHOP_TEST_DB'; Database = 'atlas_shop_test_services_' + $atlasSuffix; SelfManaged = $true },
        @{ Name = 'legacy-rename'; Flag = '--shop-rename-mysql'; Variable = 'ATLAS_SHOP_TEST_DB'; Database = 'atlas_shop_test_rename_' + $atlasSuffix; SelfManaged = $true }
    )
    foreach ($atlasRun in ($atlasRuns | Where-Object { $_.Name -in $Suites })) {
        $atlasDatabase = $atlasRun.Database
        if ($atlasDatabase -notmatch '^atlas_((auth_session|migration_ceiling)_test_shop_|shop_test_(services|rename)_)[a-f0-9]{12}$') { throw 'Unexpected disposable database name.' }
        if (!$atlasRun.SelfManaged) {
            Invoke-FixtureSql ('CREATE DATABASE ' + $atlasDatabase + ' CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;') | Out-Null
            $atlasCreated.Add($atlasDatabase)
        }
        [Environment]::SetEnvironmentVariable($atlasRun.Variable,
            'Server=127.0.0.1;Port=13307;User ID=root;Password=;Database=' + $atlasDatabase + ';Pooling=false;AllowPublicKeyRetrieval=true;SslMode=None', 'Process')
        $atlasLog = Join-Path $atlasOutputs ($atlasRun.Name + '-mysql.log')
        if ($atlasRun.Name -eq 'legacy-rename') { $env:ATLAS_SHOP_SQL_EMITTER = $SqlEmitter }
        & $DotNet $atlasTests $atlasRun.Flag *> $atlasLog
        $atlasExitCode = $LASTEXITCODE
        Get-Content -LiteralPath $atlasLog -Tail 5
        if ($atlasExitCode -ne 0) { throw $atlasRun.Name + ' integration test failed.' }
        if (!$atlasRun.SelfManaged) {
            Invoke-FixtureSql ('DROP DATABASE ' + $atlasDatabase + ';') | Out-Null
            $atlasCreated.Remove($atlasDatabase) | Out-Null
        }
        elseif ((Invoke-FixtureSql ("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME IN ('" + $atlasDatabase + "','" + $atlasDatabase + "_chars');")) -ne '0') {
            throw 'The test-owned databases were not cleaned.'
        }
    }
}
finally {
    $env:ATLAS_AUTH_SESSION_TEST_DB = $atlasSavedAuth
    $env:ATLAS_MIGRATION_CEILING_TEST_DB = $atlasSavedCeiling
    $env:ATLAS_SHOP_TEST_DB = $atlasSavedShop
    $env:ATLAS_SHOP_SQL_EMITTER = $atlasSavedEmitter
    if ($atlasOwner) {
        $atlasCurrent = Get-CimInstance Win32_Process -Filter "ProcessId=$atlasOwner"
        if ($atlasCurrent.ExecutablePath -eq $atlasDaemon -and $atlasCurrent.CommandLine.Contains($atlasConfig)) {
            try {
                foreach ($atlasDatabase in $atlasCreated) {
                    Invoke-FixtureSql ('DROP DATABASE ' + $atlasDatabase + ';') | Out-Null
                }
            }
            finally {
                & $atlasAdmin --no-defaults --protocol=TCP --host=127.0.0.1 --port=13307 --user=root shutdown
                if ($LASTEXITCODE -ne 0) { throw 'Owned MySQL did not shut down.' }
            }
        }
    }
    elseif ($atlasStarted -and !$atlasStarted.HasExited) { Stop-Process -InputObject $atlasStarted }
    if ($atlasStarted) {
        $atlasStarted.WaitForExit(15000) | Out-Null
        if (Get-NetTCPConnection -LocalPort 13307 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 13307 remains open.' }
        'Disposable databases removed; owned local MySQL stopped.'
    }
}
