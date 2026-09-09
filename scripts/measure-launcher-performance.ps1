[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory,
    [ValidateRange(1, 10)][int]$Pairs = 3
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository ('artifacts/launcher-performance-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$benchmarkRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $benchmarkRoot) { throw 'Choose a new output directory to preserve previous measurements.' }
$project = Join-Path $repository 'source/WotLK.Launcher.IntegrationTests/WotLK.Launcher.IntegrationTests.csproj'
$assembly = Join-Path $repository 'source/WotLK.Launcher.IntegrationTests/bin/Release/net8.0-windows10.0.17763.0/WotLK.Launcher.IntegrationTests.dll'

Push-Location -LiteralPath $repository
try {
    & $DotnetPath build $project -c Release -p:AtlasLocalClientBuild=true -p:NuGetAudit=false --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Performance harness build failed.' }
    New-Item -ItemType Directory -Path $benchmarkRoot | Out-Null
    $runs = @()
    foreach ($pair in 1..$Pairs) {
        $fixtureRoot = Join-Path $benchmarkRoot ('pair-' + $pair)
        foreach ($mode in @('cold', 'warm')) {
            # A new process per run; the paired warm run reuses only its fixture caches.
            & $DotnetPath $assembly --launcher-performance $fixtureRoot $mode
            if ($LASTEXITCODE -ne 0) { throw "Performance run $pair/$mode failed." }
            $runs += Get-Content -LiteralPath (Join-Path $fixtureRoot ($mode + '.json')) -Raw | ConvertFrom-Json
        }
    }
    $runs | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $benchmarkRoot 'runs.json') -Encoding utf8
    Write-Output "Performance measurements complete: $benchmarkRoot"
}
finally { Pop-Location }
