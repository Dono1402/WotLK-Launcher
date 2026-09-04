[CmdletBinding()]
param(
    [string]$DotnetPath = 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot 'AtlasLauncherLocal'
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Le client local doit rester sous $artifactsRoot."
}
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "SDK .NET introuvable : $DotnetPath"
}

$project = Join-Path $repository 'source\WotLK.Launcher\WotLK.Launcher.csproj'
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

& $DotnetPath publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -p:AtlasLocalClientBuild=true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:NuGetAudit=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "La publication du client local a échoué ($LASTEXITCODE)."
}

$publishedExecutable = Join-Path $OutputDirectory 'WotLK.Launcher.exe'
$localExecutable = Join-Path $OutputDirectory 'AtlasLauncherLocal.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Le publish ne contient pas WotLK.Launcher.exe."
}
Move-Item -LiteralPath $publishedExecutable -Destination $localExecutable

$file = Get-Item -LiteralPath $localExecutable
$hash = (Get-FileHash -LiteralPath $localExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Atlas Launcher local prêt."
Write-Output "Path=$($file.FullName)"
Write-Output "Size=$($file.Length)"
Write-Output "SHA256=$hash"
