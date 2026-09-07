[CmdletBinding()]
param(
    [string]$DotNetPath = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet/sdk-8.0.424/dotnet.exe'),
    [string]$OutputRelativePath = 'artifacts/atlas-chat-polish-20260907/server-candidate'
)

$ErrorActionPreference = 'Stop'
$candidateRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidateRoot = [IO.Path]::GetFullPath((Join-Path $candidateRepo $OutputRelativePath))
$candidateAllowed = [IO.Path]::GetFullPath((Join-Path $candidateRepo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $candidateRoot.StartsWith($candidateAllowed, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The candidate must stay under this workspace artifacts directory.'
}
if (Test-Path -LiteralPath $candidateRoot) { throw 'Candidate directory already exists; choose a new output path to preserve it.' }
if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw 'The explicit .NET8 SDK was not found.' }
$candidateSource = Join-Path $candidateRepo 'source/WotLK.Launcher.Server'
$candidateProject = Join-Path $candidateSource 'WotLK.Launcher.Server.csproj'
$candidatePublish = Join-Path $candidateRoot 'publish'
$candidateBuild = Join-Path $candidateRoot 'build-artifacts'
$candidateArchive = Join-Path $candidateRoot 'atlas-presence-api-linux-x64-schema8.tar.gz'
New-Item -ItemType Directory -Path $candidateRoot, $candidatePublish | Out-Null

# Configuration stays external. Fail closed if a source secret was accidentally populated.
$candidateSettings = Get-Content -LiteralPath (Join-Path $candidateSource 'appsettings.json') -Raw | ConvertFrom-Json
foreach ($candidateName in @('ConnectionString', 'HermesSharedSecret', 'BrevoApiKey')) {
    if (-not [string]::IsNullOrWhiteSpace([string]$candidateSettings.LauncherServer.$candidateName)) {
        throw "Source configuration contains a populated $candidateName; packaging is stopped without printing its value."
    }
}

& $DotNetPath publish $candidateProject -c Release -r linux-x64 --self-contained true `
    --artifacts-path $candidateBuild -o $candidatePublish --nologo -p:DebugSymbols=false -p:DebugType=none `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=false `
    2>&1 | Tee-Object -FilePath (Join-Path $candidateRoot 'publish.log')
if ($LASTEXITCODE -ne 0) { throw "Candidate publish failed with exit code $LASTEXITCODE." }

$candidateBinaryNames = @('WotLK.Launcher.Server', 'libSkiaSharp.so')
foreach ($candidateRequired in $candidateBinaryNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $candidatePublish $candidateRequired) -PathType Leaf)) {
        throw "Missing self-contained Linux candidate file: $candidateRequired"
    }
}
foreach ($candidateElf in @('WotLK.Launcher.Server', 'libSkiaSharp.so')) {
    $candidateElfStream = [IO.File]::OpenRead((Join-Path $candidatePublish $candidateElf))
    try {
        $candidateHeader = [byte[]]::new(20)
        $candidateElfStream.ReadExactly($candidateHeader)
        if ([Convert]::ToHexString($candidateHeader[0..3]) -ne '7F454C46' -or $candidateHeader[4] -ne 2 -or
            [BitConverter]::ToUInt16($candidateHeader, 18) -ne 62) { throw "Invalid Linux x64 ELF header: $candidateElf" }
    } finally { $candidateElfStream.Dispose() }
}

$candidateEvidence = @()
foreach ($candidateTest in @('presence-api-mysql', 'chat-v2-api-mysql', 'chat-api-mysql')) {
    $candidateLog = Join-Path $candidateRepo ('artifacts/atlas-chat-polish-20260907/' + $candidateTest + '.log')
    if (-not (Test-Path -LiteralPath $candidateLog -PathType Leaf)) {
        throw "Validated test evidence is missing: $candidateTest"
    }
    $candidateLabel = switch ($candidateTest) {
        'presence-api-mysql' { 'Presence API MySQL ' }
        'chat-v2-api-mysql' { 'Chat v2 MySQL ' }
        'chat-api-mysql' { 'Chat API MySQL ' }
    }
    $candidateLogText = Get-Content -LiteralPath $candidateLog -Raw
    $candidateResult = [regex]::Match($candidateLogText, '(?m)^' + [regex]::Escape($candidateLabel) + '[^\r\n]* PASS: (\d+) assertions')
    if (-not $candidateResult.Success -or -not $candidateLogText.Contains('Workspace-owned MySQL fixture stopped; no listener on 13307/13308.')) {
        throw "Test success or fixture cleanup evidence is missing: $candidateTest"
    }
    $candidateEvidence += [ordered]@{ name=$candidateTest; assertions=[int]$candidateResult.Groups[1].Value;
        sha256=(Get-FileHash -LiteralPath $candidateLog -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$candidateMigrationRows = @(Get-ChildItem -LiteralPath (Join-Path $candidateSource 'Database/Migrations') -Filter '*.sql' -File |
    Sort-Object Name | ForEach-Object { [ordered]@{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
if ($candidateMigrationRows.Count -ne 8 -or $candidateMigrationRows[-1].name -ne '0008_global_presence.sql') {
    throw 'This candidate must contain the eight verified migrations, including additive global presence.'
}
$candidateFiles = @($candidateBinaryNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $candidatePublish $_) } | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($candidatePublish,$_.FullName).Replace('\','/'); bytes=$_.Length;
        sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$candidateManifest = [ordered]@{
    formatVersion=1; purpose='Atlas global presence API candidate'; builtAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
    runtime='linux-x64'; selfContained=$true; singleFile=$true; publishTrimmed=$false; nativeLibrariesForSelfExtract=$false;
    schemaVersion=8; apiVersions=@(1,2); deployment=$false;
    productionAccess=$false; productionServicesRestarted=$false; nativeExecutionOnLinuxVerified=$false;
    requiredProductionConfiguration=@('WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=8','WOTLK_CHAT_MEDIA_ROOT=<private writable directory>');
    validation=$candidateEvidence; migrations=$candidateMigrationRows; fileCount=$candidateFiles.Count; files=$candidateFiles;
    boundaries=@('This archive has not been deployed.',
        'Migrations0001 through0007 are unchanged; migration0008 adds global presence and carries existing DND preferences.',
        'An old server binary rejects a schema history containing an unknown migration; binary-only rollback after migration8 is not supported.',
        'Game transport remains plain-text direct messages; already delivered game text cannot be withdrawn.',
        'Linux startup and service identity remain deployment-stage checks.');
}
$candidateManifestPath = Join-Path $candidateRoot 'candidate-manifest.json'
$candidateManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $candidateManifestPath -Encoding utf8

# Explicit POSIX modes make the apphost executable after extracting on Linux.
$candidateArchiveStream = [IO.File]::Create($candidateArchive)
$candidateGzip = [IO.Compression.GZipStream]::new($candidateArchiveStream,[IO.Compression.CompressionLevel]::Optimal,$false)
$candidateTar = [System.Formats.Tar.TarWriter]::new($candidateGzip,[System.Formats.Tar.TarEntryFormat]::Pax,$false)
try {
    foreach ($candidateFile in ($candidateBinaryNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $candidatePublish $_) } | Sort-Object FullName)) {
        $candidateRelative = [IO.Path]::GetRelativePath($candidatePublish,$candidateFile.FullName).Replace('\','/')
        $candidateEntry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile,$candidateRelative)
        $candidateEntry.Mode = [IO.UnixFileMode]$(if ($candidateRelative -eq 'WotLK.Launcher.Server') {493} else {420})
        $candidateInput = [IO.File]::OpenRead($candidateFile.FullName)
        try { $candidateEntry.DataStream=$candidateInput; $candidateTar.WriteEntry($candidateEntry) }
        finally { $candidateInput.Dispose() }
    }
} finally { $candidateTar.Dispose(); $candidateGzip.Dispose(); $candidateArchiveStream.Dispose() }

$candidateArchiveHash = (Get-FileHash -LiteralPath $candidateArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$candidateExpectedHashes = @{}
foreach ($candidateFile in $candidateFiles) { $candidateExpectedHashes[$candidateFile.path]=$candidateFile.sha256 }
$candidateVerifiedEntries = 0
$candidateReadArchive = [IO.File]::OpenRead($candidateArchive)
$candidateReadGzip = [IO.Compression.GZipStream]::new($candidateReadArchive,[IO.Compression.CompressionMode]::Decompress,$false)
$candidateReader = [System.Formats.Tar.TarReader]::new($candidateReadGzip,$false)
try {
    while ($null -ne ($candidateEntry = $candidateReader.GetNextEntry())) {
        if (-not $candidateExpectedHashes.ContainsKey($candidateEntry.Name) -or $null -eq $candidateEntry.DataStream) {
            throw "Unexpected archive entry: $($candidateEntry.Name)"
        }
        $candidateEntryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($candidateEntry.DataStream)).ToLowerInvariant()
        if ($candidateEntryHash -ne $candidateExpectedHashes[$candidateEntry.Name]) { throw "Archive content hash mismatch: $($candidateEntry.Name)" }
        if ($candidateEntry.Name -eq 'WotLK.Launcher.Server' -and [int]$candidateEntry.Mode -ne 493) { throw 'Linux apphost executable mode is missing.' }
        $candidateExpectedHashes.Remove($candidateEntry.Name)
        $candidateVerifiedEntries++
    }
    if ($candidateExpectedHashes.Count -ne 0) { throw 'Some publish files are missing from the archive.' }
} finally { $candidateReader.Dispose(); $candidateReadGzip.Dispose(); $candidateReadArchive.Dispose() }
[ordered]@{ archive=[IO.Path]::GetFileName($candidateArchive); bytes=(Get-Item -LiteralPath $candidateArchive).Length;
    sha256=$candidateArchiveHash; manifestSha256=(Get-FileHash -LiteralPath $candidateManifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
    verifiedEntries=$candidateVerifiedEntries; allContentHashesVerified=$true; linuxApphostExecutableMode='0755';
    deployment=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $candidateRoot 'archive-verification.json') -Encoding utf8
Write-Output "Linux x64 self-contained candidate packaged: $candidateArchive"
Write-Output "SHA256 $candidateArchiveHash"
Write-Output 'Deployment=false; production not contacted.'
