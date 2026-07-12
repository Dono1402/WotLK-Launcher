param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [string]$ArchiveCacheDirectory = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$PackageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
$catalogPath = Join-Path $PackageDirectory 'catalog.json'
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    throw "catalog.json absent de $PackageDirectory"
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if ([string]::IsNullOrWhiteSpace($ArchiveCacheDirectory)) {
    $ArchiveCacheDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'Atlas-AddonValidation-Downloads'
}
$ArchiveCacheDirectory = [System.IO.Path]::GetFullPath($ArchiveCacheDirectory)
New-Item -ItemType Directory -Path $ArchiveCacheDirectory -Force | Out-Null

function Resolve-Archive([string]$Url, [long]$ExpectedSize, [string]$ExpectedSha256) {
    $fileName = [System.IO.Path]::GetFileName(([Uri]$Url).AbsolutePath)
    $searchRoots = @($PackageDirectory, $ArchiveCacheDirectory) | Sort-Object -Unique
    foreach ($root in $searchRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }
        foreach ($candidate in Get-ChildItem -LiteralPath $root -Recurse -File -Filter $fileName) {
            if ($candidate.Length -eq $ExpectedSize -and (Get-Sha256 $candidate.FullName) -eq $ExpectedSha256) {
                return $candidate.FullName
            }
        }
    }

    $destination = Join-Path $ArchiveCacheDirectory $fileName
    Invoke-WebRequest -Uri $Url -OutFile $destination -UseBasicParsing
    if ((Get-Item -LiteralPath $destination).Length -ne $ExpectedSize -or (Get-Sha256 $destination) -ne $ExpectedSha256) {
        throw "Archive distante invalide: $Url"
    }

    return $destination
}

function Normalize-StripPrefix([string]$Prefix) {
    if ($null -eq $Prefix) {
        return ''
    }
    return $Prefix.Replace('\', '/').Trim('/')
}

function Expand-ValidatedArchive(
    [string]$ArchivePath,
    [string]$Destination,
    [string[]]$AllowedRoots,
    [string]$StripPrefix
) {
    $rootSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $AllowedRoots) {
        [void]$rootSet.Add($root)
    }

    $normalizedPrefix = Normalize-StripPrefix $StripPrefix
    $prefixWithSeparator = if ($normalizedPrefix) { $normalizedPrefix + '/' } else { '' }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)

    try {
        foreach ($entry in $archive.Entries) {
            $normalized = $entry.FullName.Replace('\', '/').TrimStart('/')
            if ([string]::IsNullOrWhiteSpace($normalized)) {
                continue
            }

            $archiveSegments = @($normalized -split '/' | Where-Object { $_ -ne '' })
            if ($archiveSegments.Count -eq 0 -or @($archiveSegments | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_.Contains(':') }).Count -ne 0) {
                throw "Chemin dangereux dans l'archive: $($entry.FullName)"
            }

            if ($normalizedPrefix) {
                if ($normalized.TrimEnd('/') -ieq $normalizedPrefix) {
                    continue
                }
                if (-not $normalized.StartsWith($prefixWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                $normalized = $normalized.Substring($prefixWithSeparator.Length)
            }

            $segments = @($normalized -split '/' | Where-Object { $_ -ne '' })
            if ($segments.Count -eq 0) {
                continue
            }
            if (-not $rootSet.Contains($segments[0])) {
                if ($normalizedPrefix) {
                    continue
                }
                throw "Racine interdite dans l'archive: $($entry.FullName)"
            }

            $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixType -eq 0xA000) {
                throw "Lien symbolique interdit dans l'archive: $($entry.FullName)"
            }

            $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $Destination ($segments -join '\')))
            $destinationPrefix = [System.IO.Path]::GetFullPath($Destination) + [System.IO.Path]::DirectorySeparatorChar
            if (-not $destinationPath.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Chemin hors destination: $($entry.FullName)"
            }

            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
                continue
            }

            if (Test-Path -LiteralPath $destinationPath) {
                throw "Collision de fichiers pendant l'assemblage: $destinationPath"
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destinationPath, $false)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ReferencedFiles([string]$EntryFile) {
    $pending = [System.Collections.Generic.Queue[string]]::new()
    $visited = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending.Enqueue([System.IO.Path]::GetFullPath($EntryFile))

    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        if (-not $visited.Add($current)) {
            continue
        }
        if (-not (Test-Path -LiteralPath $current -PathType Leaf)) {
            throw "Reference absente: $current"
        }

        $extension = [System.IO.Path]::GetExtension($current)
        $references = @()
        $content = [System.IO.File]::ReadAllText($current)
        if ($extension -ieq '.toc') {
            $references = @($content -split "`r?`n" |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ -ne '' -and -not $_.StartsWith('#') -and ($_ -match '\.(lua|xml)$') })
        }
        elseif ($extension -ieq '.xml') {
            $activeXml = [regex]::Replace($content, '<!--[\s\S]*?-->', '')
            $references = @([regex]::Matches($activeXml, '(?i)<(?:Script|Include)\b[^>]*\bfile\s*=\s*[''"]([^''"]+)[''"]') |
                ForEach-Object { $_.Groups[1].Value })
        }

        foreach ($reference in $references) {
            $resolved = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $current) $reference.Replace('/', '\')))
            if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
                throw "Reference absente depuis $current : $reference"
            }
            if ([System.IO.Path]::GetExtension($resolved) -in @('.toc', '.xml')) {
                $pending.Enqueue($resolved)
            }
        }
    }
}

$catalogText = [System.IO.File]::ReadAllText($catalogPath, [System.Text.Encoding]::UTF8)
$catalog = $catalogText | ConvertFrom-Json
if ($catalog.schemaVersion -ne 1 -or $catalog.clientInterface -ne '30403') {
    throw 'Catalogue incompatible.'
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("Atlas-AddonValidation-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results = @()

try {
    foreach ($addon in $catalog.addons) {
        $extractPath = Join-Path $testRoot $addon.id
        New-Item -ItemType Directory -Path $extractPath -Force | Out-Null

        $archivePath = Resolve-Archive $addon.url ([long]$addon.size) $addon.sha256
        Expand-ValidatedArchive $archivePath $extractPath @($addon.folders) $addon.stripPrefix
        $totalBytes = (Get-Item -LiteralPath $archivePath).Length

        foreach ($component in @($addon.components)) {
            $componentArchive = Resolve-Archive $component.url ([long]$component.size) $component.sha256
            Expand-ValidatedArchive $componentArchive $extractPath @($addon.folders) $component.stripPrefix
            $totalBytes += (Get-Item -LiteralPath $componentArchive).Length
        }

        $replacements = @($addon.tokenReplacements.PSObject.Properties)
        if ($replacements.Count -gt 0) {
            foreach ($file in Get-ChildItem -LiteralPath $extractPath -Recurse -File | Where-Object { $_.Extension -in @('.toc', '.lua', '.xml') }) {
                $content = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
                $updated = $content
                foreach ($replacement in $replacements) {
                    $updated = $updated.Replace($replacement.Name, [string]$replacement.Value)
                }
                if ($updated -ne $content) {
                    [System.IO.File]::WriteAllText($file.FullName, $updated, [System.Text.UTF8Encoding]::new($false))
                }
            }
        }

        foreach ($folder in $addon.folders) {
            $folderPath = Join-Path $extractPath $folder
            $tocs = @(Get-ChildItem -LiteralPath $folderPath -Filter '*.toc' -File)
            if ($tocs.Count -eq 0) {
                throw "TOC absent: $folder"
            }
            $matchingTocs = @($tocs | Where-Object {
                [System.IO.File]::ReadAllText($_.FullName) -match '(?m)^## Interface:[^\r\n]*\b30403\b'
            })
            if ($matchingTocs.Count -eq 0) {
                throw "Interface 30403 absente: $folder"
            }
            foreach ($toc in $matchingTocs) {
                Assert-ReferencedFiles $toc.FullName
            }
        }

        $unresolved = @(Get-ChildItem -LiteralPath $extractPath -Recurse -File |
            Where-Object { $_.Extension -in @('.toc', '.lua', '.xml') } |
            Where-Object { [System.IO.File]::ReadAllText($_.FullName) -match '@project-[^@]+@' })
        if ($unresolved.Count -ne 0) {
            throw "Jeton de build non resolu dans $($unresolved[0].FullName)"
        }

        $results += [PSCustomObject]@{
            Id = $addon.id
            Version = $addon.version
            Interface = $addon.interface
            Folders = @($addon.folders).Count
            Files = @(Get-ChildItem -LiteralPath $extractPath -Recurse -File).Count
            Bytes = $totalBytes
            Sha256 = $addon.installHash
        }
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$results | Format-Table -AutoSize
