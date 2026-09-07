[CmdletBinding()]
param(
    [string]$DotnetPath = 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe',
    [string]$OutputDirectory,
    [string]$ArmoryNodePath = (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe')
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
$armoryNode = [IO.Path]::GetFullPath($ArmoryNodePath)
$armoryServer = Join-Path $repository 'prototypes\armory-3d\launcher-server.cjs'
if (-not (Test-Path -LiteralPath $armoryNode -PathType Leaf) -or
    -not (Test-Path -LiteralPath $armoryServer -PathType Leaf)) {
    throw "Les dépendances de l'armurerie locale sont introuvables. Vérifiez ArmoryNodePath et prototypes/armory-3d."
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

@{ NodePath = $armoryNode; ServerPath = $armoryServer } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'armory-local.json') -Encoding utf8

$file = Get-Item -LiteralPath $localExecutable
$hash = (Get-FileHash -LiteralPath $localExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Atlas Launcher local prêt."
Write-Output "Path=$($file.FullName)"
Write-Output "Size=$($file.Length)"
Write-Output "SHA256=$hash"
