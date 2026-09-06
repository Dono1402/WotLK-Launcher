[CmdletBinding()]
param(
    [string]$NodePath = (Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'),
    [string]$VendorRoot,
    [string]$MetadataRoot,
    [string]$WebViewInstallerPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$dependencies = Join-Path $artifactsRoot 'atlas-release-140/dependencies'
if (!$VendorRoot) { $VendorRoot = Join-Path $artifactsRoot 'armory-prototype/tools/wow-export' }
if (!$MetadataRoot) { $MetadataRoot = Join-Path $artifactsRoot 'armory-prototype/metadata' }
if (!$WebViewInstallerPath) { $WebViewInstallerPath = Join-Path $dependencies 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe' }
if (!$OutputPath) { $OutputPath = Join-Path $artifactsRoot 'atlas-release-140/armory-runtime.zip' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (!$OutputPath.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Le paquet d’armurerie doit être créé sous artifacts."
}
New-Item -ItemType Directory -Path $dependencies -Force | Out-Null

# Node 24.19.0 x64, verified against the upstream SHASUMS256.txt.
$nodeSha256 = '3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237'
if (!(Test-Path -LiteralPath $NodePath -PathType Leaf) -or (Get-FileHash -LiteralPath $NodePath).Hash -ne $nodeSha256) {
    throw 'Node 24.19.0 x64 officiel est requis ; fournissez son node.exe avec -NodePath.'
}
$nodeLicense = Join-Path $dependencies 'NODE-LICENSE.txt'
if (!(Test-Path -LiteralPath $nodeLicense)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/nodejs/node/v24.19.0/LICENSE' -OutFile $nodeLicense -TimeoutSec 30
}
if (!(Test-Path -LiteralPath $WebViewInstallerPath)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?LinkId=2124701' -OutFile $WebViewInstallerPath -TimeoutSec 600
}
$runtimeSignature = Get-AuthenticodeSignature -LiteralPath $WebViewInstallerPath
if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "La signature Microsoft de l’installateur WebView2 est invalide."
}

$stage = [IO.Path]::GetFullPath((Join-Path $artifactsRoot ('atlas-release-140/runtime-stage-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $stage | Out-Null
function Add-RuntimeFile([string]$Source, [string]$Relative) {
    if (!(Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Dépendance absente : $Source" }
    $target = [IO.Path]::GetFullPath((Join-Path $stage $Relative))
    if (!$target.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Chemin de paquet invalide.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    # Copy file contents even when pnpm exposes a package as links or junctions.
    [IO.File]::Copy((Get-Item -LiteralPath $Source).FullName, $target, $true)
}
function Add-RuntimeTree([string]$Source, [string]$Relative) {
    $directory = Get-Item -LiteralPath $Source
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { $directory = $directory.ResolveLinkTarget($true) }
    foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File -Recurse -Force) {
        $suffix = [IO.Path]::GetRelativePath($directory.FullName, $file.FullName)
        Add-RuntimeFile $file.FullName (Join-Path $Relative $suffix)
    }
}

try {
    Add-RuntimeFile $NodePath 'node/node.exe'
    Add-RuntimeFile $nodeLicense 'node/LICENSE.txt'
    Add-RuntimeFile $WebViewInstallerPath 'prerequisites/MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
    $app = Join-Path $repository 'prototypes/armory-3d'
    $applicationFiles = @(
        'launcher-server.cjs','launcher-rpc.cjs','launcher-armory.cjs','launcher-roster.cjs',
        'launcher-models.cjs','launcher-icons.cjs','runtime-paths.cjs','viewer-assets.cjs',
        'armory-data.cjs','combat-statistics.cjs','armory-cache.cjs','statistics-cache.cjs',
        'export-pipeline.cjs','local-client.cjs','prepare.cjs','export.cjs','texture-compositor.cjs',
        'equipment-rendering.cjs','resolve-item-tables.cjs','item-details.cjs',
        'launcher.html','launcher.js','launcher.css','index.html','app.js','style.css',
        'inter-fonts.css','i18n.mjs','character-labels.mjs','character-stats.mjs'
    )
    foreach ($name in $applicationFiles) { Add-RuntimeFile (Join-Path $app $name) ('app/' + $name) }
    Add-RuntimeTree (Join-Path $VendorRoot 'src/js') 'vendor/wow-export/src/js'
    Add-RuntimeFile (Join-Path $VendorRoot 'LICENSE') 'vendor/wow-export/LICENSE'
    Add-RuntimeFile (Join-Path $VendorRoot 'LEGAL') 'vendor/wow-export/LEGAL'
    foreach ($name in @('three','lucide','webp-wasm')) {
        $module = Join-Path $app ('node_modules/' + $name)
        if ($name -eq 'three') {
            foreach ($file in @('build/three.module.js','build/three.core.js','examples/jsm/loaders/GLTFLoader.js',
                'examples/jsm/controls/OrbitControls.js','examples/jsm/utils/BufferGeometryUtils.js')) {
                Add-RuntimeFile (Join-Path $module $file) ('app/node_modules/three/' + $file)
            }
            Add-RuntimeFile (Join-Path $module 'package.json') 'app/node_modules/three/package.json'
            Add-RuntimeFile (Join-Path $module 'LICENSE') 'app/node_modules/three/LICENSE'
        } elseif ($name -eq 'lucide') {
            Add-RuntimeFile (Join-Path $module 'dist/umd/lucide.min.js') 'app/node_modules/lucide/dist/umd/lucide.min.js'
            Add-RuntimeFile (Join-Path $module 'package.json') 'app/node_modules/lucide/package.json'
            Add-RuntimeFile (Join-Path $module 'LICENSE') 'app/node_modules/lucide/LICENSE'
        } else {
            foreach ($file in @('package.json','LICENSE.md','index.js','webp_node_enc.js','webp_node_enc.wasm','webp_node_dec.js','webp_node_dec.wasm')) {
                Add-RuntimeFile (Join-Path $module $file) ('app/node_modules/' + $name + '/' + $file)
            }
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $MetadataRoot -File) {
        if ($file.Extension -eq '.dbd' -or $file.Name -in @('manifest.json','public-tact.txt')) {
            Add-RuntimeFile $file.FullName ('metadata/' + $file.Name)
        }
    }
    $assets = Join-Path $repository 'source/WotLK.Launcher/Assets'
    foreach ($weight in @('Regular','Medium','SemiBold','ExtraBold')) {
        Add-RuntimeFile (Join-Path $assets ('Fonts/Inter-' + $weight + '.ttf')) ('assets/Fonts/Inter-' + $weight + '.ttf')
    }
    Add-RuntimeFile (Join-Path $assets 'Fonts/OFL-Inter.txt') 'assets/Fonts/OFL-Inter.txt'
    Add-RuntimeFile (Join-Path $assets 'Launcher/visuals/icecrown-citadel.png') 'assets/Launcher/visuals/icecrown-citadel.png'
    Add-RuntimeTree (Join-Path $assets 'Launcher/class-icons') 'assets/Launcher/class-icons'
    $manifestFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    [ordered]@{schemaVersion=1;nodeVersion='24.19.0';files=$manifestFiles} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    $temporaryZip = $OutputPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporaryZip -Destination $OutputPath -Force
    [ordered]@{path=$OutputPath;files=$manifestFiles.Count;size=(Get-Item -LiteralPath $OutputPath).Length;sha256=(Get-FileHash -LiteralPath $OutputPath).Hash;nodeSha256=$nodeSha256;webViewSignature='Microsoft Corporation / Valid';containsPlayerData=$false;requiresSsh=$false} |
        ConvertTo-Json | Set-Content -LiteralPath ($OutputPath + '.json') -Encoding utf8
    Get-Content -LiteralPath ($OutputPath + '.json')
}
finally {
    if ((Split-Path -Parent $stage) -eq (Join-Path $artifactsRoot 'atlas-release-140') -and
        (Split-Path -Leaf $stage) -match '^runtime-stage-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
