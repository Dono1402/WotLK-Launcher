[CmdletBinding()]
param([switch]$Install, [switch]$ConfirmOnlyWeakAuras5129)

# Default: read-only preflight. Installation requires BOTH explicit switches.
# No game/launcher is started or stopped. No registry or live WTF file is written.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$AddonRoot = 'C:\Program Files (x86)\WotLK\_classic_\Interface\AddOns'
$InterfaceRoot = 'C:\Program Files (x86)\WotLK\_classic_\Interface'
$AccountRoot = 'C:\Program Files (x86)\WotLK\_classic_\WTF\Account'
$ClientBinary = 'C:\Program Files (x86)\WotLK\_classic_\WowClassic.exe'
$PreparedRoot = Join-Path $PSScriptRoot 'extracted\weakauras-legacy-5.12.9'
$ZipPath = Join-Path $PSScriptRoot 'downloads\WeakAuras-5.12.9.zip'
$ZipSha256 = '24e72072d22669f157d4acb44d7f0bbfceadbbe598d41285e013346891dcdeab'
$BackupParent = Join-Path $env:LOCALAPPDATA 'Atlas\Backups\WeakAuras'
$AddonNames = @('WeakAuras', 'WeakAurasArchive', 'WeakAurasModelPaths', 'WeakAurasOptions', 'WeakAurasTemplates')
$SavedVariableNames = @('WeakAuras.lua', 'WeakAuras.lua.bak', 'WeakAurasArchive.lua', 'WeakAurasArchive.lua.bak', 'WeakAurasOptions.lua', 'WeakAurasOptions.lua.bak')

function Assert-NoReparseAncestor([string]$Path) {
    $part = [IO.Path]::GetFullPath($Path)
    while ($part) {
        if (Test-Path -LiteralPath $part) {
            if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'A required path has a reparse-point ancestor; operation refused.'
            }
        }
        $parent = [IO.Directory]::GetParent($part)
        if ($null -eq $parent) { break }
        $part = $parent.FullName
    }
}

function Assert-ChildPath([string]$Path, [string]$Boundary) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Boundary).TrimEnd('\') + '\'
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An operation target escapes its explicitly allowed directory.'
    }
    Assert-NoReparseAncestor $full
}

function Get-ActiveClients {
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(Wow|WowClassic|WowClassicT|WowB|WowT|Battle\.net|Agent|AtlasLauncherLocal|WotLK\.Launcher|AtlasLauncher)\.exe$'
    } | Select-Object Name, ProcessId)
}

function Assert-ClientsClosed {
    if (@(Get-ActiveClients).Count) { throw 'A game or launcher process is active. Close it manually before installation.' }
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-AddonSnapshot([string]$Base) {
    $snapshot = [ordered]@{}
    foreach ($name in $AddonNames) {
        $root = Join-Path $Base $name
        Assert-ChildPath $root $Base
        $items = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
        if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw 'A WeakAuras tree contains a reparse point.'
        }
        foreach ($file in @($items | Where-Object { !$_.PSIsContainer } | Sort-Object FullName)) {
            $key = $file.FullName.Substring($Base.TrimEnd('\').Length + 1)
            $snapshot[$key] = [string]$file.Length + '|' + (Get-Sha256 $file.FullName)
        }
    }
    return $snapshot
}

function Assert-SameSnapshot($Expected, $Actual, [string]$Label) {
    if ($Expected.Count -ne $Actual.Count) { throw ($Label + ': file inventory changed.') }
    foreach ($key in $Expected.Keys) {
        if (!$Actual.Contains($key) -or $Actual[$key] -ne $Expected[$key]) {
            throw ($Label + ': file hash or size differs.')
        }
    }
}

function Get-ProtectedSnapshot {
    $snapshot = [ordered]@{}
    $records = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'protected-baseline.json') -Raw | ConvertFrom-Json
    foreach ($record in $records) {
        Assert-ChildPath $record.path $AddonRoot
        $digest = Get-Sha256 $record.path
        if ($digest -ne $record.sha256) { throw 'A protected ElvUI/SDM/registry baseline differs.' }
        $snapshot[$record.path] = $digest
    }
    if ($snapshot.Count -ne 5) { throw 'Unexpected protected baseline inventory.' }
    return $snapshot
}

function Get-SavedVariableFiles {
    $profiles = @()
    foreach ($account in @(Get-ChildItem -LiteralPath $AccountRoot -Directory -Force)) {
        Assert-ChildPath $account.FullName $AccountRoot
        $directory = Join-Path $account.FullName 'SavedVariables'
        Assert-ChildPath $directory $AccountRoot
        $files = @()
        foreach ($name in $SavedVariableNames) {
            $path = Join-Path $directory $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Assert-ChildPath $path $AccountRoot
                $files += Get-Item -LiteralPath $path -Force
            }
        }
        if ($files.Count) { $profiles += [pscustomobject]@{Directory=$directory; Files=$files} }
    }
    if ($profiles.Count -ne 1 -or $profiles[0].Files.Count -ne 6) {
        throw 'SavedVariables account selection differs from the reviewed one-account/six-file inventory.'
    }
    return $profiles[0]
}

function Get-SavedVariableSnapshot($Files) {
    $snapshot = [ordered]@{}
    foreach ($file in $Files) { $snapshot[$file.Name] = [string]$file.Length + '|' + (Get-Sha256 $file.FullName) }
    return $snapshot
}

foreach ($path in @($AddonRoot, $AccountRoot, $PreparedRoot, $BackupParent, $ClientBinary)) { Assert-NoReparseAncestor $path }
if ((Get-Item -LiteralPath $ClientBinary).VersionInfo.FileVersion -ne '3.4.3.54261') { throw 'Unexpected client build.' }
if ((Get-Sha256 $ZipPath) -ne $ZipSha256 -or (Get-Item -LiteralPath $ZipPath).Length -ne 8887511) { throw 'Pinned ZIP differs.' }
$preparedChildren = @(Get-ChildItem -LiteralPath $PreparedRoot -Force)
if ($preparedChildren.Count -ne 5 -or @($preparedChildren | Where-Object { !$_.PSIsContainer -or $_.Name -notin $AddonNames }).Count) {
    throw 'Unexpected prepared root layout.'
}
$candidate = Get-AddonSnapshot $PreparedRoot
$installed = Get-AddonSnapshot $AddonRoot
if ($candidate.Count -ne 675 -or $installed.Count -ne 675) { throw 'Expected exactly 675 candidate and installed files.' }
$changed = 0
foreach ($key in $candidate.Keys) {
    if (!$installed.Contains($key)) { throw 'Unexpected added or removed WeakAuras file.' }
    if ($candidate[$key] -ne $installed[$key]) { $changed++ }
}
if ($changed -ne 59) { throw 'Installed WeakAuras delta differs from the reviewed 59 files.' }
foreach ($name in $AddonNames) {
    foreach ($record in @(@{Base=$AddonRoot; Version='5.12.8'}, @{Base=$PreparedRoot; Version='5.12.9'})) {
        $toc = Get-Content -LiteralPath (Join-Path (Join-Path $record.Base $name) ($name + '_Wrath.toc')) -Raw
        if ($toc -notmatch '(?m)^## Interface: 30403\r?$' -or $toc -notmatch ('(?m)^## Version: ' + [regex]::Escape($record.Version) + '\r?$')) {
            throw 'Unexpected WeakAuras Wrath TOC version/interface.'
        }
    }
}
$zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
$zipSnapshot = [ordered]@{}
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $key = $entry.FullName.Replace('/', '\')
        if (!$candidate.Contains($key) -or $zipSnapshot.Contains($key)) { throw 'ZIP and extracted file inventory differ.' }
        $stream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $hasher.Dispose() }
        $zipSnapshot[$key] = [string]$entry.Length + '|' + $digest
    }
} finally { $zip.Dispose() }
Assert-SameSnapshot $zipSnapshot $candidate 'Candidate versus pinned ZIP'
$protected = Get-ProtectedSnapshot
$savedVariables = Get-SavedVariableFiles
$active = @(Get-ActiveClients)
$preflight = [ordered]@{mode='read-only-preflight'; client='3.4.3.54261'; fromVersion='5.12.8'; toVersion='5.12.9'; zipSha256=$ZipSha256;
    addonRoots=$AddonNames; files=675; changed=59; added=0; removed=0; savedVariableAccounts=1; savedVariableFiles=6;
    savedVariableBytes=($savedVariables.Files | Measure-Object Length -Sum).Sum; savedVariableContentsInspected=$false;
    protectedBaselinesMatch=$true; activeProcesses=$active; readyForInstallation=($active.Count -eq 0); privateBackupParent=$BackupParent}
if (!$Install) { $preflight | ConvertTo-Json -Depth 5; return }
if (!$ConfirmOnlyWeakAuras5129) { throw 'Installation requires -Install -ConfirmOnlyWeakAuras5129 after separate approval.' }
Assert-ClientsClosed
if ((Get-PSDrive C).Free -lt 1GB) { throw 'Less than 1 GiB available for retained backup/staging copies.' }

# All following writes are authorized only in explicitly selected WeakAuras paths.
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')
$backup = Join-Path $BackupParent ('5.12.8-to-5.12.9-' + $stamp)
$stage = Join-Path $InterfaceRoot ('.atlas-wa-install-' + $stamp)
Assert-ChildPath $backup $BackupParent
Assert-ChildPath $stage $InterfaceRoot
if ((Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $stage)) { throw 'A unique backup/staging destination already exists.' }
New-Item -ItemType Directory -Path $backup | Out-Null
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$sids = @([Security.Principal.WindowsIdentity]::GetCurrent().User,
    [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
foreach ($sid in $sids) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inheritance, 'None', 'Allow')) }
Set-Acl -LiteralPath $backup -AclObject $acl
foreach ($name in @('installed-copy', 'moved-originals', 'SavedVariables', 'failed-candidate')) {
    New-Item -ItemType Directory -Path (Join-Path $backup $name) | Out-Null
}
New-Item -ItemType Directory -Path $stage | Out-Null
$savedBefore = Get-SavedVariableSnapshot $savedVariables.Files
foreach ($name in $AddonNames) {
    Copy-Item -LiteralPath (Join-Path $AddonRoot $name) -Destination (Join-Path (Join-Path $backup 'installed-copy') $name) -Recurse
    Copy-Item -LiteralPath (Join-Path $PreparedRoot $name) -Destination (Join-Path $stage $name) -Recurse
}
foreach ($file in $savedVariables.Files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path (Join-Path $backup 'SavedVariables') $file.Name) }
Assert-SameSnapshot $installed (Get-AddonSnapshot (Join-Path $backup 'installed-copy')) 'Installed code backup'
Assert-SameSnapshot $candidate (Get-AddonSnapshot $stage) 'Prepared candidate copy'
$savedCopyFiles = @(Get-ChildItem -LiteralPath (Join-Path $backup 'SavedVariables') -File -Force)
Assert-SameSnapshot $savedBefore (Get-SavedVariableSnapshot $savedCopyFiles) 'SavedVariables backup'
$privateRecord = [ordered]@{fromVersion='5.12.8'; toVersion='5.12.9'; accountSavedVariablesDirectory=$savedVariables.Directory;
    installed=$installed; candidate=$candidate; savedVariables=$savedBefore; savedVariablesNeverOverwritten=$true}
[IO.File]::WriteAllText((Join-Path $backup 'private-restore-manifest.json'), ($privateRecord | ConvertTo-Json -Depth 8))
$allowedBackupSids = @($sids | ForEach-Object { $_.Value })
$privateBackupFiles = @($savedCopyFiles.FullName) + @(Join-Path $backup 'private-restore-manifest.json')
foreach ($privateFile in $privateBackupFiles) {
    foreach ($rule in (Get-Acl -LiteralPath $privateFile).Access) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {
            $ruleSid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
            if ($ruleSid -notin $allowedBackupSids) {
                throw 'A private backup file grants access outside the user/SYSTEM/Administrators allowlist; replacement refused.'
            }
        }
    }
}
Assert-SameSnapshot $installed (Get-AddonSnapshot $AddonRoot) 'Installed code before replacement'
Assert-SameSnapshot $savedBefore (Get-SavedVariableSnapshot (Get-SavedVariableFiles).Files) 'SavedVariables before replacement'
Assert-SameSnapshot $protected (Get-ProtectedSnapshot) 'Protected files before replacement'
Assert-ClientsClosed
$moved = @()
$placed = @()
try {
    foreach ($name in $AddonNames) {
        Assert-ClientsClosed
        $old = Join-Path $AddonRoot $name
        $retained = Join-Path (Join-Path $backup 'moved-originals') $name
        $prepared = Join-Path $stage $name
        Assert-ChildPath $old $AddonRoot
        Assert-ChildPath $retained (Join-Path $backup 'moved-originals')
        Assert-ChildPath $prepared $stage
        Move-Item -LiteralPath $old -Destination $retained
        $moved += $name
        Move-Item -LiteralPath $prepared -Destination $old
        $placed += $name
    }
    Assert-SameSnapshot $candidate (Get-AddonSnapshot $AddonRoot) 'Installed 5.12.9 files'
    Assert-SameSnapshot $installed (Get-AddonSnapshot (Join-Path $backup 'moved-originals')) 'Retained original directories'
    Assert-SameSnapshot $savedBefore (Get-SavedVariableSnapshot (Get-SavedVariableFiles).Files) 'Live SavedVariables untouched'
    Assert-SameSnapshot $protected (Get-ProtectedSnapshot) 'Protected files after replacement'
} catch {
    # Never restore files underneath a newly opened client. Preserve every copy.
    if (@(Get-ActiveClients).Count -eq 0) {
        foreach ($name in $placed) {
            $current = Join-Path $AddonRoot $name
            $failed = Join-Path (Join-Path $backup 'failed-candidate') $name
            Assert-ChildPath $current $AddonRoot
            Assert-ChildPath $failed (Join-Path $backup 'failed-candidate')
            Move-Item -LiteralPath $current -Destination $failed
        }
        foreach ($name in $moved) {
            $original = Join-Path (Join-Path $backup 'moved-originals') $name
            $restore = Join-Path $AddonRoot $name
            Assert-ChildPath $original (Join-Path $backup 'moved-originals')
            Assert-ChildPath $restore $AddonRoot
            Move-Item -LiteralPath $original -Destination $restore
        }
        Assert-SameSnapshot $installed (Get-AddonSnapshot $AddonRoot) 'Automatic code rollback'
    }
    throw ('Installation did not finish. All backups retained at ' + $backup + '. No SavedVariables were overwritten. ' + $_.Exception.Message)
}
[ordered]@{installed=$true; version='5.12.9'; verifiedFiles=675; savedVariableBackups=6; liveSavedVariablesUnchanged=$true;
    protectedBaselinesUnchanged=$true; backup=$backup; retainedEmptyStaging=$stage; gameOrLauncherStarted=$false; runtimeValidated=$false} | ConvertTo-Json -Depth 5
