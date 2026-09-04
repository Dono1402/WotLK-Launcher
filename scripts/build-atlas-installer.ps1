[CmdletBinding()]
param(
    [string]$DotnetPath = 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe',
    [string]$OutputDirectory,
    [string]$LauncherPayloadPath
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
$expectedFileVersion = '1.3.0.0'
$expectedProductVersion = '1.3.0'

function Get-PeMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "SDK .NET introuvable : $DotnetPath"
}

if (-not [string]::IsNullOrWhiteSpace($LauncherPayloadPath)) {
    $LauncherPayloadPath = [IO.Path]::GetFullPath($LauncherPayloadPath)
    if (-not $LauncherPayloadPath.StartsWith(
            $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Le launcher canonique doit provenir de $artifactsRoot."
    }
    if (-not (Test-Path -LiteralPath $LauncherPayloadPath -PathType Leaf)) {
        throw "Launcher canonique introuvable : $LauncherPayloadPath"
    }

    $candidate = Get-Item -LiteralPath $LauncherPayloadPath
    $candidateHash = (Get-FileHash -LiteralPath $LauncherPayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (($candidate.VersionInfo.FileVersion -ne $expectedFileVersion) -or
        ($candidate.VersionInfo.ProductVersion -ne $expectedProductVersion)) {
        throw "Les métadonnées du launcher canonique 1.3.0 sont invalides."
    }
    if ((Get-PeMachine $LauncherPayloadPath) -ne 0x8664) {
        throw "Le launcher canonique 1.3.0 n'est pas un exécutable x64."
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $payload) -Force | Out-Null
    Copy-Item -LiteralPath $LauncherPayloadPath -Destination $payload -Force
}

$payloadFile = Get-Item -LiteralPath $payload
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
if (($payloadFile.VersionInfo.FileVersion -ne $expectedFileVersion) -or
    ($payloadFile.VersionInfo.ProductVersion -ne $expectedProductVersion)) {
    throw "Les métadonnées du payload Atlas Launcher 1.3.0 sont invalides."
}
if ((Get-PeMachine $payload) -ne 0x8664) {
    throw "Le payload Atlas Launcher 1.3.0 n'est pas un exécutable x64."
}
if ((-not [string]::IsNullOrWhiteSpace($LauncherPayloadPath)) -and
    ($payloadFile.Length -ne $candidate.Length -or $payloadHash -ne $candidateHash)) {
    throw "La copie du launcher canonique vers le payload a été altérée."
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
        -p:NuGetAudit=false `
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

    $machine = Get-PeMachine $target
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
