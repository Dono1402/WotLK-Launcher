[CmdletBinding()]
param(
    [string]$DotnetPath = (Join-Path $env:USERPROFILE '.dotnet/sdk-8.0.424/dotnet.exe'),
    [string]$OutputDirectory,
    [string]$ArmoryPayloadPath
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $artifactsRoot 'AtlasLauncherPublic' }
if (!$ArmoryPayloadPath) {
    & (Join-Path $PSScriptRoot 'build-armory-runtime.ps1')
    $ArmoryPayloadPath = Join-Path $artifactsRoot 'atlas-release-140/armory-runtime.zip'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$ArmoryPayloadPath = [IO.Path]::GetFullPath($ArmoryPayloadPath)
if (!$OutputDirectory.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Le client public doit être préparé sous artifacts.'
}
if (!(Test-Path -LiteralPath $ArmoryPayloadPath -PathType Leaf)) { throw 'Paquet armurerie absent.' }
$project = Join-Path $repository 'source/WotLK.Launcher/WotLK.Launcher.csproj'
$stage = Join-Path $artifactsRoot ('atlas-release-140/publish-' + [Guid]::NewGuid().ToString('N'))
try {
    & $DotnetPath publish $project -c Release -r win-x64 --self-contained true --nologo `
        -p:AtlasLocalClientBuild=false -p:PublishSingleFile=true -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        "-p:ArmoryPayloadPath=$ArmoryPayloadPath" -o $stage
    if ($LASTEXITCODE -ne 0) { throw "Publication du client échouée ($LASTEXITCODE)." }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $stage -File) {
        $name = if ($file.Name -eq 'WotLK.Launcher.exe') { 'WotLK-Launcher.exe' } else { $file.Name }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $OutputDirectory $name) -Force
    }
    $executable = Get-Item -LiteralPath (Join-Path $OutputDirectory 'WotLK-Launcher.exe')
    [ordered]@{path=$executable.FullName;version=$executable.VersionInfo.ProductVersion;size=$executable.Length;sha256=(Get-FileHash -LiteralPath $executable.FullName).Hash;armoryPayloadSha256=(Get-FileHash -LiteralPath $ArmoryPayloadPath).Hash} | ConvertTo-Json
}
finally {
    $stage = [IO.Path]::GetFullPath($stage)
    if ((Split-Path -Parent $stage) -eq (Join-Path $artifactsRoot 'atlas-release-140') -and
        (Split-Path -Leaf $stage) -match '^publish-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
