[CmdletBinding()]
param(
    [string]$DotnetPath = 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository 'artifacts\AtlasLauncherSetup'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Le dossier de sortie doit rester sous $artifactsRoot."
}

$project = Join-Path $repository 'source\WotLK.Launcher.Installer\WotLK.Launcher.Installer.csproj'
$payload = Join-Path $repository 'source\WotLK.Launcher.Installer\Payload\WotLK.Launcher.exe'
$expectedLength = 79820116L
$expectedHash = '690f0afed2010affef628115f6602815d9017e20189224300b79e3885c7ab2b6'

if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "SDK .NET introuvable : $DotnetPath"
}

$payloadFile = Get-Item -LiteralPath $payload
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
if ($payloadFile.Length -ne $expectedLength -or $payloadHash -ne $expectedHash) {
    throw "Le payload Atlas Launcher 1.1.2 ne correspond pas à la build validée."
}

$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) ("AtlasLauncherSetup-publish-" + [Guid]::NewGuid().ToString('N'))
$publishDirectory = [IO.Path]::GetFullPath($publishDirectory)
if (-not $publishDirectory.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Le dossier temporaire de publication est invalide."
}
try {
    & $DotnetPath publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --nologo `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "La publication de l'installateur a échoué ($LASTEXITCODE)."
    }

    $source = Join-Path $publishDirectory 'WotLK.Launcher.Installer.exe'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw 'Le publish ne contient pas le binaire attendu.'
    }

    if (Test-Path -LiteralPath $OutputDirectory) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    $target = Join-Path $OutputDirectory 'AtlasLauncherSetup.exe'
    Copy-Item -LiteralPath $source -Destination $target

    $bytes = [IO.File]::ReadAllBytes($target)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x8664) {
        throw ('Architecture PE inattendue : 0x{0:x4}' -f $machine)
    }

    $artifact = Get-Item -LiteralPath $target
    [pscustomobject]@{
        Path = $artifact.FullName
        Length = $artifact.Length
        SHA256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        Architecture = 'x64 (PE32+ AMD64)'
        SetupVersion = $artifact.VersionInfo.FileVersion
        PayloadVersion = $payloadFile.VersionInfo.FileVersion
        PayloadLength = $payloadFile.Length
        PayloadSHA256 = $payloadHash
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}
