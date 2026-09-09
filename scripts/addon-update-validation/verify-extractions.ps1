param([Parameter(Mandatory=$true)][string]$StageRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$addonVerifyStage = (Resolve-Path -LiteralPath $StageRoot).Path
if (Get-Item -LiteralPath $addonVerifyStage,(Join-Path $addonVerifyStage 'extracted'),(Join-Path $addonVerifyStage 'downloads') | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked stage/extraction/archive parent rejected' }
$addonVerifyManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../docs/update-preparation/2026-09-09/addons-manifest.json') -Raw | ConvertFrom-Json
$addonVerifyResults = @()
foreach ($addonVerifyPackage in $addonVerifyManifest.packages) {
    $addonVerifyKey = if ($addonVerifyPackage.version -eq '5.12.9') { 'weakauras-legacy' } else { $addonVerifyPackage.id }
    $addonVerifyArchivePath = Join-Path $addonVerifyStage ('downloads/' + $addonVerifyPackage.filename)
    $addonVerifyRoot = [IO.Path]::GetFullPath((Join-Path $addonVerifyStage ('extracted/' + $addonVerifyKey + '-' + $addonVerifyPackage.version)))
    $addonVerifyRootPrefix = $addonVerifyRoot + [IO.Path]::DirectorySeparatorChar
    $addonVerifyDigest = (Get-FileHash -LiteralPath $addonVerifyArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($addonVerifyDigest -ne $addonVerifyPackage.sha256) { throw "Pinned ZIP SHA256 mismatch: $addonVerifyKey" }
    $addonVerifyReparse = @(Get-Item -LiteralPath $addonVerifyRoot) + @(Get-ChildItem -LiteralPath $addonVerifyRoot -Recurse -Force)
    if ($addonVerifyReparse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Reparse point in extraction: $addonVerifyKey" }
    $addonVerifyZip = [IO.Compression.ZipFile]::OpenRead($addonVerifyArchivePath)
    $addonVerifyPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $addonVerifyCount = 0
    try {
        foreach ($addonVerifyEntry in $addonVerifyZip.Entries) {
            $addonVerifyName = $addonVerifyEntry.FullName.Replace('\','/')
            $addonVerifyTarget = [IO.Path]::GetFullPath((Join-Path $addonVerifyRoot $addonVerifyName))
            if (-not $addonVerifyTarget.StartsWith($addonVerifyRootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected archive traversal: $addonVerifyName" }
            if (-not $addonVerifyPaths.Add($addonVerifyName.TrimEnd('/'))) { throw "Duplicate ZIP entry: $addonVerifyName" }
            if ($addonVerifyName.EndsWith('/')) { continue }
            $addonVerifyFile = Get-Item -LiteralPath $addonVerifyTarget
            if ($addonVerifyFile.PSIsContainer -or $addonVerifyFile.Length -ne $addonVerifyEntry.Length) { throw "Entry size mismatch: $addonVerifyName" }
            $addonVerifyStream = $addonVerifyEntry.Open()
            $addonVerifyHasher = [Security.Cryptography.SHA256]::Create()
            try {
                $addonVerifyEntryHash = [BitConverter]::ToString($addonVerifyHasher.ComputeHash($addonVerifyStream)).Replace('-','').ToLowerInvariant()
            } finally { $addonVerifyStream.Dispose(); $addonVerifyHasher.Dispose() }
            if ((Get-FileHash -LiteralPath $addonVerifyTarget -Algorithm SHA256).Hash.ToLowerInvariant() -ne $addonVerifyEntryHash) { throw "Extracted content mismatch: $addonVerifyName" }
            $addonVerifyCount++
        }
    } finally { $addonVerifyZip.Dispose() }
    $addonVerifyFiles = @(Get-ChildItem -LiteralPath $addonVerifyRoot -Recurse -File -Force)
    if ($addonVerifyFiles.Count -ne $addonVerifyCount -or $addonVerifyCount -ne $addonVerifyPackage.validation.files) { throw "Unexpected extracted file count: $addonVerifyKey" }
    $addonVerifyResults += [pscustomobject]@{id=$addonVerifyPackage.id;version=$addonVerifyPackage.version;archiveSha256=$addonVerifyDigest;verifiedFiles=$addonVerifyCount;status='pass'}
}
$addonVerifyResults | ConvertTo-Json -Depth 4
