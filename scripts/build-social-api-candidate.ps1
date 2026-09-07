[CmdletBinding()]
param(
    [string]$DotNetPath = 'C:/Users/Dono/.dotnet/sdk-8.0.424/dotnet.exe',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$socialRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$socialRoot = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $socialRepo 'artifacts/atlas-social-corrections' }
$socialPublish = Join-Path $socialRoot 'api-publish'
$socialBuild = Join-Path $socialRoot 'api-build'
New-Item -ItemType Directory -Path $socialRoot -Force | Out-Null
$socialLog = Join-Path $socialRoot 'api-publish.log'
& $DotNetPath publish (Join-Path $socialRepo 'source/WotLK.Launcher.Server/WotLK.Launcher.Server.csproj') `
    -c Release -r linux-x64 --self-contained true --artifacts-path $socialBuild `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false `
    -o $socialPublish *> $socialLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $socialLog -Tail 35; throw 'API candidate publish failed.' }
$socialNames = @('WotLK.Launcher.Server','libSkiaSharp.so')
$socialFiles = foreach ($socialName in $socialNames) {
    $socialPath = Join-Path $socialPublish $socialName
    $socialItem = Get-Item -LiteralPath $socialPath
    [pscustomobject]@{
        Name = $socialName
        Size = $socialItem.Length
        SHA256 = (Get-FileHash -LiteralPath $socialPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$socialArchive = Join-Path $socialRoot 'atlas-api-social-candidate.tar.gz'
& tar.exe -czf $socialArchive -C $socialPublish @socialNames
if ($LASTEXITCODE -ne 0) { throw 'API candidate archive failed.' }
$socialEntries = @(& tar.exe -tzf $socialArchive)
if ($LASTEXITCODE -ne 0 -or (($socialEntries | Sort-Object) -join "`n") -ne (($socialNames | Sort-Object) -join "`n")) {
    throw 'Unexpected API archive entries.'
}
$socialMigrations = foreach ($socialMigration in Get-ChildItem -LiteralPath (Join-Path $socialRepo 'source/WotLK.Launcher.Server/Database/Migrations') -Filter '*.sql' | Sort-Object Name) {
    [pscustomobject]@{ Name = $socialMigration.Name; SHA256 = (Get-FileHash -LiteralPath $socialMigration.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$socialManifest = [ordered]@{
    PreparedUtc = [DateTime]::UtcNow.ToString('o')
    Platform = 'linux-x64'
    SelfContained = $true
    Files = @($socialFiles)
    Archive = $socialArchive
    ArchiveSHA256 = (Get-FileHash -LiteralPath $socialArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    Migrations = @($socialMigrations)
    RequiredChatSchemaCeiling = 6
    ServerDeploymentAuthorized = $false
    Deployed = $false
    GameModuleIncluded = $false
    Scope = 'Authenticated friend profiles, Atlas launcher messaging, and server player count'
}
$socialManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $socialRoot 'api-candidate.json') -Encoding utf8
$socialManifest | ConvertTo-Json -Depth 6
