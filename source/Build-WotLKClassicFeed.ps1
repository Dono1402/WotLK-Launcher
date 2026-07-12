[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ClientRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [Parameter(Mandatory = $true)]
    [string]$AtlasLauncherPath,

    [string]$Version = ("wotlk-classic-3.4.3.54261-frFR-{0}" -f (Get-Date -Format "yyyy.MM.dd.HHmm")),
    [string]$BaseUrl = "http://152.228.225.7/wotlk/"
)

$ErrorActionPreference = "Stop"

function Get-NormalizedPath {
    param([string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Assert-SafeOutputPath {
    param(
        [string]$Source,
        [string]$Output
    )

    $sourceFull = Get-NormalizedPath -Path $Source
    $outputFull = Get-NormalizedPath -Path $Output
    $driveRoot = [System.IO.Path]::GetPathRoot($outputFull).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)

    if ([string]::Equals($sourceFull, $outputFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputRoot must not be the client directory."
    }
    if ([string]::Equals($driveRoot, $outputFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a drive root as OutputRoot: $outputFull"
    }
    if ($sourceFull.StartsWith($outputFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputRoot must not contain ClientRoot."
    }
}

function Copy-DirectoryWithRobocopy {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$ExcludedDirectories = @(),
        [string[]]$ExcludedFiles = @()
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $arguments = @(
        $Source,
        $Destination,
        "/E",
        "/COPY:DAT",
        "/DCOPY:DAT",
        "/R:2",
        "/W:1",
        "/NFL",
        "/NDL",
        "/NJH",
        "/NJS",
        "/NP"
    )
    if ($ExcludedDirectories.Count -gt 0) {
        $arguments += "/XD"
        $arguments += $ExcludedDirectories
    }
    if ($ExcludedFiles.Count -gt 0) {
        $arguments += "/XF"
        $arguments += $ExcludedFiles
    }

    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Robocopy failed from $Source to $Destination with exit code $LASTEXITCODE."
    }
}

function Get-Sha256 {
    param([string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $hash = $sha.ComputeHash($stream)
            return -join ($hash | ForEach-Object { $_.ToString("x2") })
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Text
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

$clientFull = Get-NormalizedPath -Path $ClientRoot
$outputFull = Get-NormalizedPath -Path $OutputRoot
$atlasLauncherFull = Get-NormalizedPath -Path $AtlasLauncherPath
$classicRoot = Join-Path $clientFull "_classic_"
$wowClassic = Join-Path $classicRoot "WowClassic.exe"
$dataRoot = Join-Path $clientFull "Data"
$buildInfo = Join-Path $clientFull ".build.info"

foreach ($requiredPath in @($clientFull, $classicRoot, $wowClassic, $dataRoot, $buildInfo, $atlasLauncherFull)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required client path not found: $requiredPath"
    }
}

Assert-SafeOutputPath -Source $clientFull -Output $outputFull
if (Test-Path -LiteralPath $outputFull) {
    Remove-Item -LiteralPath $outputFull -Recurse -Force
}

$filesRoot = Join-Path $outputFull "files"
New-Item -ItemType Directory -Force -Path $filesRoot | Out-Null

Write-Host "Copying CASC data..."
Copy-DirectoryWithRobocopy `
    -Source $dataRoot `
    -Destination (Join-Path $filesRoot "Data") `
    -ExcludedFiles @("shmem", "RepairMarker.psv", "*.lru", "*.lock", "*.lck", "*.tmp")

Write-Host "Copying Classic binaries..."
Copy-DirectoryWithRobocopy `
    -Source $classicRoot `
    -Destination (Join-Path $filesRoot "_classic_") `
    -ExcludedDirectories @("Cache", "Errors", "Interface", "Logs", "Screenshots", "WTF") `
    -ExcludedFiles @("*.bak", "*.dmp", "*.log", "*.tmp")

Copy-Item -LiteralPath $buildInfo -Destination (Join-Path $filesRoot ".build.info") -Force
$productDb = Join-Path $clientFull ".product.db"
if (Test-Path -LiteralPath $productDb) {
    Copy-Item -LiteralPath $productDb -Destination (Join-Path $filesRoot ".product.db") -Force
}
Copy-Item -LiteralPath $atlasLauncherFull -Destination (Join-Path $filesRoot "Arctium Game Launcher Atlas.exe") -Force

$requiredFeedFiles = @(
    ".build.info",
    "Arctium Game Launcher Atlas.exe",
    "_classic_\WowClassic.exe"
)
foreach ($relativePath in $requiredFeedFiles) {
    $path = Join-Path $filesRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required feed file not found after copy: $relativePath"
    }
}

$forbiddenDirectoryNames = @("Cache", "Errors", "Logs", "Screenshots", "WTF")
$privateFiles = Get-ChildItem -LiteralPath $filesRoot -Recurse -File -Force | Where-Object {
    $relative = $_.FullName.Substring($filesRoot.Length).TrimStart('\', '/')
    $segments = $relative -split '[\\/]'
    $segments | Where-Object { $forbiddenDirectoryNames -contains $_ }
}
if ($privateFiles) {
    throw "Private or mutable files leaked into the feed: $($privateFiles[0].FullName)"
}

$runtimeFiles = Get-ChildItem -LiteralPath $filesRoot -Recurse -File -Force | Where-Object {
    $_.Name -eq "shmem" -or
    $_.Name -eq "RepairMarker.psv" -or
    $_.Name -like "*.lru" -or
    $_.Name -like "*.lock" -or
    $_.Name -like "*.lck" -or
    $_.Name -like "*.tmp"
}
if ($runtimeFiles) {
    throw "Runtime CASC files leaked into the feed: $($runtimeFiles[0].FullName)"
}

Write-Host "Hashing feed..."
$manifestFiles = New-Object System.Collections.Generic.List[object]
$allFiles = @(Get-ChildItem -LiteralPath $filesRoot -Recurse -File -Force | Sort-Object FullName)
$index = 0
foreach ($file in $allFiles) {
    $index++
    $relative = $file.FullName.Substring($filesRoot.Length).TrimStart('\').Replace('\', '/')
    Write-Progress -Activity "Hashing WotLK Classic feed" -Status $relative -PercentComplete (($index / [Math]::Max($allFiles.Count, 1)) * 100)
    $manifestFiles.Add([ordered]@{
        path = $relative
        size = $file.Length
        sha256 = Get-Sha256 -Path $file.FullName
    })
}
Write-Progress -Activity "Hashing WotLK Classic feed" -Completed

$generatedAt = (Get-Date).ToUniversalTime().ToString("o")
$manifest = [ordered]@{
    version = $Version
    baseUrl = $BaseUrl
    generatedAt = $generatedAt
    files = $manifestFiles
}
$manifestJson = $manifest | ConvertTo-Json -Depth 5
Write-Utf8NoBom -Path (Join-Path $outputFull "manifest.json") -Text ($manifestJson + [Environment]::NewLine)

$totalBytes = ($allFiles | Measure-Object Length -Sum).Sum
$summary = [ordered]@{
    version = $Version
    generatedAt = $generatedAt
    build = "3.4.3.54261"
    locale = "frFR"
    portal = "animeclub.fr"
    files = $allFiles.Count
    bytes = $totalBytes
}
Write-Utf8NoBom -Path (Join-Path $outputFull "feed-summary.json") -Text (($summary | ConvertTo-Json) + [Environment]::NewLine)

Write-Host ("Feed created: {0}" -f $outputFull) -ForegroundColor Green
Write-Host ("Files: {0}, size: {1:n2} GiB" -f $allFiles.Count, ($totalBytes / 1GB)) -ForegroundColor Green
