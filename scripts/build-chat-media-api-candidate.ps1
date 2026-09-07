#requires -Version 7.4
<#
.SYNOPSIS
Builds a local Linux x64 API candidate for the expanded Messages media formats.
.DESCRIPTION
Run only after the media sources are frozen and both local test commands have
passed: --chat-media and --chat-v2-api-mysql. Supply their complete logs and a
previously verified schema8 candidate-manifest.json. Relative input paths resolve
from the repository root. The MySQL log must also contain the fixture runner's
post-cleanup line: Workspace-owned MySQL fixture stopped; no listener on 13307/13308.
The variant mentioning only port 13307 is accepted. Do not append this line until
the owned fixture has actually stopped and its listeners have been checked.

All evidence and migration hashes are checked before publish. Output must be a
new directory beneath this repository's artifacts directory. The archive contains
only WotLK.Launcher.Server and libSkiaSharp.so; settings and credentials remain
external. This script neither runs the Linux binary nor deploys, opens SSH, starts
services or changes a database. Inspect candidate-manifest.json and
archive-verification.json before a separately authorized deployment.
.EXAMPLE
./scripts/build-chat-media-api-candidate.ps1 `
    -ChatMediaTestLogPath 'artifacts/current-validation/chat-media.log' `
    -ChatV2ApiMySqlTestLogPath 'artifacts/current-validation/chat-v2-api-mysql.log' `
    -Schema8BaselineManifestPath 'artifacts/verified-schema8/candidate-manifest.json' `
    -OutputRelativePath 'artifacts/current-validation/server-candidate'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ChatMediaTestLogPath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ChatV2ApiMySqlTestLogPath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Schema8BaselineManifestPath,
    [string]$DotNetPath = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet/sdk-8.0.424/dotnet.exe'),
    [string]$OutputRelativePath = 'artifacts/atlas-chat-followup-20260907/server-candidate'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$candidateRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-CandidateInput([string]$Path, [string]$Label) {
    $resolved = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $candidateRepo $Path }))
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Missing $Label file." }
    return $resolved
}

function Get-CandidateSourceRows {
    $files = @(foreach ($folder in @('source/WotLK.Launcher.Server', 'source/WotLK.Launcher.Chat.Contracts')) {
        Get-ChildItem -LiteralPath (Join-Path $candidateRepo $folder) -Recurse -File | Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' -and $_.Extension -in @('.cs', '.csproj', '.sql', '.json', '.props', '.targets')
        }
    })
    foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.Config')) {
        $path = Join-Path $candidateRepo $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files += Get-Item -LiteralPath $path }
    }
    return @($files | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path=[IO.Path]::GetRelativePath($candidateRepo, $_.FullName).Replace('\','/');
            sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}

if ([IO.Path]::IsPathRooted($OutputRelativePath)) { throw 'OutputRelativePath must be relative to this repository.' }
$candidateRoot = [IO.Path]::GetFullPath((Join-Path $candidateRepo $OutputRelativePath))
$candidateAllowed = [IO.Path]::GetFullPath((Join-Path $candidateRepo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $candidateRoot.StartsWith($candidateAllowed, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The candidate must stay under this workspace artifacts directory.'
}
if (Test-Path -LiteralPath $candidateRoot) { throw 'Candidate directory already exists; choose a new output path to preserve it.' }
$DotNetPath = Resolve-CandidateInput $DotNetPath '.NET SDK'
$candidateSource = Join-Path $candidateRepo 'source/WotLK.Launcher.Server'
$candidateProject = Join-Path $candidateSource 'WotLK.Launcher.Server.csproj'
$candidatePublish = Join-Path $candidateRoot 'publish'
$candidateBuild = Join-Path $candidateRoot 'build-artifacts'
$candidateArchive = Join-Path $candidateRoot 'atlas-chat-media-api-linux-x64-schema8.tar.gz'

# Required evidence is supplied explicitly; an unrelated historical PASS is not
# silently substituted by selecting an old artifact directory.
$candidateEvidence = @()
foreach ($test in @(
    @{ Name='chat-media'; Path=$ChatMediaTestLogPath; Pattern='(?m)^Chat rich media PASS: ([1-9][0-9]*) assertions\.' },
    @{ Name='chat-v2-api-mysql'; Path=$ChatV2ApiMySqlTestLogPath; Pattern='(?m)^Chat v2 MySQL [^\r\n]+ PASS: ([1-9][0-9]*) assertions\.' }
)) {
    $log = Resolve-CandidateInput $test.Path ($test.Name + ' test evidence')
    $logText = Get-Content -LiteralPath $log -Raw
    $result = [regex]::Matches($logText, $test.Pattern)
    if ($result.Count -ne 1 -or $logText -match '(?im)^\s*(?:Unhandled exception\b|FAIL(?:ED)?\b|SKIP(?:PED)?\b)') {
        throw "Exactly one successful, non-skipped test run is required: $($test.Name)"
    }
    if ($test.Name -eq 'chat-v2-api-mysql' -and $logText -notmatch '(?m)^Workspace-owned MySQL fixture stopped; no listener on 13307(?:/13308)?\.[\t ]*\r?$') {
        throw 'The MySQL test log lacks verified fixture shutdown/listener cleanup evidence.'
    }
    $candidateEvidence += [ordered]@{ name=$test.Name; command='--' + $test.Name;
        assertions=[int]$result[0].Groups[1].Value; logFile=[IO.Path]::GetFileName($log);
        sha256=(Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant();
        fixtureCleanupVerified=($test.Name -eq 'chat-v2-api-mysql') }
}

$candidateBaselinePath = Resolve-CandidateInput $Schema8BaselineManifestPath 'schema8 baseline manifest'
$candidateBaseline = Get-Content -LiteralPath $candidateBaselinePath -Raw | ConvertFrom-Json
if ($candidateBaseline.schemaVersion -ne 8 -or @($candidateBaseline.migrations).Count -ne 8) {
    throw 'The baseline manifest must describe exactly eight verified schema8 migrations.'
}
$candidateExpectedMigrations = @{}
foreach ($row in $candidateBaseline.migrations) {
    if ($row.name -notmatch '^000[1-8]_[a-z0-9_]+\.sql$' -or $row.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $candidateExpectedMigrations.ContainsKey($row.name)) {
        throw 'The baseline has an invalid or duplicate migration record.'
    }
    $candidateExpectedMigrations[$row.name] = $row.sha256.ToLowerInvariant()
}
$candidateMigrationRows = @(Get-ChildItem -LiteralPath (Join-Path $candidateSource 'Database/Migrations') -Filter '*.sql' -File |
    Sort-Object Name | ForEach-Object { [ordered]@{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
if ($candidateMigrationRows.Count -ne 8 -or $candidateMigrationRows[-1].name -ne '0008_global_presence.sql') {
    throw 'This media candidate must retain schema8; adding or removing migrations is outside its scope.'
}
foreach ($row in $candidateMigrationRows) {
    if (-not $candidateExpectedMigrations.ContainsKey($row.name) -or $candidateExpectedMigrations[$row.name] -ne $row.sha256) {
        throw "Migration differs from the verified schema8 baseline: $($row.name)"
    }
}

# Never print configuration values when reporting a populated secret.
foreach ($settingsFile in Get-ChildItem -LiteralPath $candidateSource -Filter 'appsettings*.json' -File) {
    $settings = Get-Content -LiteralPath $settingsFile.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in @('ConnectionString', 'HermesSharedSecret', 'BrevoApiKey')) {
        if ($settings.ContainsKey('LauncherServer') -and $settings.LauncherServer.ContainsKey($name) -and
            -not [string]::IsNullOrWhiteSpace([string]$settings.LauncherServer[$name])) {
            throw "Source configuration contains a populated $name; packaging stopped without printing its value."
        }
    }
}
$candidateSourceRows = @(Get-CandidateSourceRows)
$candidateSourceSignature = ConvertTo-Json -InputObject $candidateSourceRows -Depth 4 -Compress
$candidateSdkVersion = (& $DotNetPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $candidateSdkVersion -notmatch '^8\.[0-9]+\.[0-9]+(?:[-+][a-zA-Z0-9.]+)?$') {
    throw 'This candidate requires a working .NET8 SDK.'
}
New-Item -ItemType Directory -Path $candidateRoot, $candidatePublish | Out-Null

& $DotNetPath publish $candidateProject -c Release -r linux-x64 --self-contained true `
    --artifacts-path $candidateBuild -o $candidatePublish --nologo -p:DebugSymbols=false -p:DebugType=none `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=false `
    2>&1 | Tee-Object -FilePath (Join-Path $candidateRoot 'publish.log')
if ($LASTEXITCODE -ne 0) { throw "Candidate publish failed with exit code $LASTEXITCODE." }
if ((ConvertTo-Json -InputObject @(Get-CandidateSourceRows) -Depth 4 -Compress) -cne $candidateSourceSignature) {
    throw 'API source inputs changed during publish. Preserve this output for inspection and rebuild after freezing the sources.'
}

$candidateBinaryNames = @('WotLK.Launcher.Server', 'libSkiaSharp.so')
foreach ($name in $candidateBinaryNames) {
    $path = Join-Path $candidatePublish $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Linux candidate file: $name" }
    $stream = [IO.File]::OpenRead($path)
    try {
        $header = [byte[]]::new(20); $stream.ReadExactly($header)
        if ([Convert]::ToHexString($header[0..3]) -ne '7F454C46' -or $header[4] -ne 2 -or $header[5] -ne 1 -or
            [BitConverter]::ToUInt16($header, 18) -ne 62) { throw "Invalid Linux x64 ELF header: $name" }
    } finally { $stream.Dispose() }
}
$candidateFiles = @($candidateBinaryNames | Sort-Object | ForEach-Object {
    $file = Get-Item -LiteralPath (Join-Path $candidatePublish $_)
    [ordered]@{ path=$file.Name; bytes=$file.Length; mode=$(if ($file.Name -eq 'WotLK.Launcher.Server') {'0755'} else {'0644'});
        sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$candidateManifestPath = Join-Path $candidateRoot 'candidate-manifest.json'
[ordered]@{
    formatVersion=2; purpose='Atlas Messages media format API candidate'; builtAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
    runtime='linux-x64'; sdkVersion=$candidateSdkVersion; selfContained=$true; singleFile=$true; publishTrimmed=$false; nativeLibrariesForSelfExtract=$false;
    schemaVersion=8; schemaMigrationsChanged=$false; apiVersions=@(1,2); deployment=$false; productionAccess=$false;
    productionServicesRestarted=$false; nativeExecutionOnLinuxVerified=$false;
    requiredProductionConfiguration=@('WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=8', 'WOTLK_CHAT_MEDIA_ROOT=<private writable directory>');
    validation=$candidateEvidence; migrations=$candidateMigrationRows;
    schemaBaseline=[ordered]@{ file=[IO.Path]::GetFileName($candidateBaselinePath);
        sha256=(Get-FileHash -LiteralPath $candidateBaselinePath -Algorithm SHA256).Hash.ToLowerInvariant(); allMigrationHashesMatch=$true };
    sourceInputs=$candidateSourceRows; sourceInputsStableDuringPublish=$true;
    fileCount=$candidateFiles.Count; files=$candidateFiles;
    boundaries=@('Local candidate only; no deployment or production contact.',
        'Schema8 migrations0001 through0008 exactly match the supplied verified baseline; no database migration is added.',
        'The media format registry and container validation are included; client playback still depends on its codecs.',
        'Configuration and secrets are external and excluded from the two-file archive.',
        'Linux startup, private media directory access and service identity require separate deployment-stage verification.')
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $candidateManifestPath -Encoding utf8

# Fixed archive metadata plus explicit POSIX modes preserve predictable contents.
$archiveStream = [IO.File]::Create($candidateArchive)
$gzip = [IO.Compression.GZipStream]::new($archiveStream, [IO.Compression.CompressionLevel]::Optimal, $false)
$tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
try {
    foreach ($file in $candidateFiles) {
        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $file.path)
        $entry.Mode = [IO.UnixFileMode]$(if ($file.path -eq 'WotLK.Launcher.Server') {493} else {420})
        $entry.ModificationTime = [DateTimeOffset]::UnixEpoch; $entry.Uid = 0; $entry.Gid = 0
        $entry.UserName = ''; $entry.GroupName = ''
        $inputStream = [IO.File]::OpenRead((Join-Path $candidatePublish $file.path))
        try { $entry.DataStream = $inputStream; $tar.WriteEntry($entry) } finally { $inputStream.Dispose() }
    }
} finally { $tar.Dispose(); $gzip.Dispose(); $archiveStream.Dispose() }

$candidateExpectedFiles = @{}; foreach ($file in $candidateFiles) { $candidateExpectedFiles[$file.path] = $file }
$candidateVerifiedEntries = 0
$readArchive = [IO.File]::OpenRead($candidateArchive)
$readGzip = [IO.Compression.GZipStream]::new($readArchive, [IO.Compression.CompressionMode]::Decompress, $false)
$reader = [System.Formats.Tar.TarReader]::new($readGzip, $false)
try {
    while ($null -ne ($entry = $reader.GetNextEntry())) {
        if (-not $candidateExpectedFiles.ContainsKey($entry.Name) -or $entry.EntryType -ne [System.Formats.Tar.TarEntryType]::RegularFile -or $null -eq $entry.DataStream) {
            throw "Unexpected or duplicate archive entry: $($entry.Name)"
        }
        $expected = $candidateExpectedFiles[$entry.Name]
        $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.DataStream)).ToLowerInvariant()
        if ($entryHash -ne $expected.sha256 -or $entry.Length -ne $expected.bytes) { throw "Archive content mismatch: $($entry.Name)" }
        $expectedMode = if ($entry.Name -eq 'WotLK.Launcher.Server') {493} else {420}
        if ([int]$entry.Mode -ne $expectedMode) { throw "Incorrect Linux archive mode: $($entry.Name)" }
        $candidateExpectedFiles.Remove($entry.Name); $candidateVerifiedEntries++
    }
    if ($candidateExpectedFiles.Count -ne 0 -or $candidateVerifiedEntries -ne 2) { throw 'The archive must contain exactly the two verified Linux files.' }
} finally { $reader.Dispose(); $readGzip.Dispose(); $readArchive.Dispose() }
$candidateArchiveHash = (Get-FileHash -LiteralPath $candidateArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{ archive=[IO.Path]::GetFileName($candidateArchive); bytes=(Get-Item -LiteralPath $candidateArchive).Length;
    sha256=$candidateArchiveHash; manifestSha256=(Get-FileHash -LiteralPath $candidateManifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
    verifiedEntries=$candidateVerifiedEntries; allContentHashesVerified=$true; linuxApphostExecutableMode='0755';
    linuxNativeLibraryMode='0644'; deployment=$false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $candidateRoot 'archive-verification.json') -Encoding utf8
Write-Output "Linux x64 Messages media candidate packaged: $candidateArchive"
Write-Output "SHA256 $candidateArchiveHash"
Write-Output 'Deployment=false; production not contacted; Linux execution not yet verified.'
