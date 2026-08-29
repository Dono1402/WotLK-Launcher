param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\addons'),
    [string]$WorkDirectory = (Join-Path $env:TEMP 'Atlas-WotLK-AddonBuild'),
    [string]$PublicBaseUrl = 'https://animeclub.fr/wotlk/addons/packages'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$WorkDirectory = [System.IO.Path]::GetFullPath($WorkDirectory)
if ($WorkDirectory.Length -lt 12 -or $WorkDirectory -eq [System.IO.Path]::GetPathRoot($WorkDirectory)) {
    throw "Dossier de travail refuse: $WorkDirectory"
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StringSha256([string]$Value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Get-VerifiedFile(
    [string]$Url,
    [string]$Destination,
    [string]$ExpectedSha256
) {
    if (-not (Test-Path -LiteralPath $Destination) -or (Get-Sha256 $Destination) -ne $ExpectedSha256) {
        $parent = Split-Path -Parent $Destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
    }

    $actualSha256 = Get-Sha256 $Destination
    if ($actualSha256 -ne $ExpectedSha256) {
        throw "SHA-256 invalide pour $Url`nAttendu: $ExpectedSha256`nRecu: $actualSha256"
    }
}

function Copy-Folder([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Dossier source introuvable: $Source"
    }

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function Merge-Folder([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $file.Name) -Force
    }
    foreach ($directory in Get-ChildItem -LiteralPath $Source -Directory) {
        Merge-Folder $directory.FullName (Join-Path $Destination $directory.Name)
    }
}

function Expand-Zip([string]$Archive, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination)
}

function Find-OnlyDirectory([string]$Parent) {
    $directories = @(Get-ChildItem -LiteralPath $Parent -Directory)
    if ($directories.Count -ne 1) {
        throw "Une seule racine etait attendue dans $Parent"
    }

    return $directories[0].FullName
}

function Assert-ZipRoots([string]$ArchivePath, [string[]]$ExpectedRoots) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $roots = @($archive.Entries |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.FullName) } |
            ForEach-Object { ($_.FullName.Replace('\', '/') -split '/')[0] } |
            Sort-Object -Unique)
        $difference = @(Compare-Object ($ExpectedRoots | Sort-Object) ($roots | Sort-Object))
        if ($difference.Count -ne 0) {
            throw "Racines inattendues dans $ArchivePath : $($roots -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}

$sources = @(
    [ordered]@{
        Id = 'weakauras'
        File = 'WeakAuras-5.12.8.zip'
        Url = 'https://github.com/WeakAuras/WeakAuras2/releases/download/5.12.8/WeakAuras-5.12.8.zip'
        Sha256 = '11b1e50ee43fb99685cb53312a1cb6dfd5c0f8299befb47137f79a2bd2889746'
    },
    [ordered]@{
        Id = 'questie'
        File = 'Questie-v10.19.2.zip'
        Url = 'https://github.com/Questie/Questie/releases/download/v10.19.2/Questie-v10.19.2.zip'
        Sha256 = 'd3d3728ea2e65ee5128eb7864cf9d71e185dfc02f85fb2563951f591144b79b8'
    },
    [ordered]@{
        Id = 'elvui-source'
        File = 'v13.61.zip'
        Url = 'https://github.com/tukui-org/ElvUI/archive/refs/tags/v13.61.zip'
        Sha256 = 'ab341194212839accfa76544096430422ebea69ad3a3b213196e8362b908c425'
    },
    [ordered]@{
        Id = 'ace3'
        File = 'Ace3-9d5c137.zip'
        Url = 'https://github.com/WoWUIDev/Ace3/archive/9d5c1375a4026bb1079fa68a5da654ec3af3a8af.zip'
        Sha256 = '8e6e49158f37ceb722c2e88d2419dcdefe6b816f652430eb3b4a663e71163919'
    },
    [ordered]@{
        Id = 'libdispel'
        File = 'LibDispel-b785912.zip'
        Url = 'https://github.com/tukui-org/LibDispel/archive/b78591237911b3feda06a38c03781a10c278b864.zip'
        Sha256 = '4d6076ce15d945f52c61dca17e8eac27cd5e82aa24272cfde1c610fe50daf40a'
    },
    [ordered]@{
        Id = 'libdualspec'
        File = 'LibDualSpec-df092db.zip'
        Url = 'https://github.com/AdiAddons/LibDualSpec-1.0/archive/df092db78365307e3930fabf298058d9d4d7a968.zip'
        Sha256 = '329954ccc28ab20431652c292b299b76ddcb4b3634af36b9d1a88cda5f7fc70b'
    },
    [ordered]@{
        Id = 'libtranslit'
        File = 'LibTranslit-66a5aa7.zip'
        Url = 'https://github.com/Vardex/LibTranslit/archive/66a5aa7df5bb404003cfba85a27bbae87c3d3536.zip'
        Sha256 = '958776d2ffbfd7015ee9573c966dcf5452ea06974b254be46f72957f64c99037'
    },
    [ordered]@{
        Id = 'dbm-core-source'
        File = 'DBM-Core-11.0.34.zip'
        Url = 'https://edge.forgecdn.net/files/5961/449/DBM-Core-11.0.34.zip'
        Sha256 = '8363dfbaca6ed7ce6a66528e7e226ecb8e16cf7f23f9be49e251c86abc21333e'
    },
    [ordered]@{
        Id = 'dbm-wotlk'
        File = 'DBM-Raids-WoTLK-r337.zip'
        Url = 'https://edge.forgecdn.net/files/5763/677/DBM-Raids-WoTLK-r337.zip'
        Sha256 = '3a869eb49e8930fa8ce85902939e6535287a953cb30daed38b420c22ca4b31a8'
    },
    [ordered]@{
        Id = 'dbm-dungeons'
        File = 'DBM-Party-WotLK-r122-wrath.zip'
        Url = 'https://edge.forgecdn.net/files/5194/699/DBM-Party-WotLK-r122-wrath.zip'
        Sha256 = 'd2f7a54b1cc433a47dafb24b4c8badbf25e443675c01c4279d6be2112ff4b0ec'
    },
    [ordered]@{
        Id = 'dbm-legacy'
        File = 'DBM-Vanilla_SoD_BC-r713.zip'
        Url = 'https://edge.forgecdn.net/files/5237/506/DBM-Vanilla_SoD_BC-r713.zip'
        Sha256 = 'dd228f7e65f6cf53129b2b92ad6ef38433980de92856fd53a7ea447ba6719532'
    },
    [ordered]@{
        Id = 'details'
        File = 'Details-Details.20250119.13388.161.zip'
        Url = 'https://edge.forgecdn.net/files/6102/986/Details-Details.20250119.13388.161.zip'
        Sha256 = 'e8a5d367db44a540cb85005c092ffd9cf32f59e44c946cffc7070509cf91a303'
    },
    [ordered]@{
        Id = 'atlaslootclassic'
        File = 'AtlasLootClassic-v3.2.0.zip'
        Url = 'https://edge.forgecdn.net/files/4811/104/AtlasLootClassic-v3.2.0.zip'
        Sha256 = '06aa9fa4c3de314422f45c67f2a555849e9f90657a980a8c7b922107b6aa90af'
    },
    [ordered]@{
        Id = 'auctionator'
        File = 'Auctionator-10.2.0-wrath.zip'
        Url = 'https://edge.forgecdn.net/files/4869/540/Auctionator-10.2.0-wrath.zip'
        Sha256 = '819f9e7aa63c3bbeb6437bbf0743cfb248f653f3dd97de6c321e563e9ea076e1'
    },
    [ordered]@{
        Id = 'leatrix-plus'
        File = 'Leatrix_Plus-3.0.191.zip'
        Url = 'https://edge.forgecdn.net/files/5275/306/Leatrix_Plus-3.0.191.zip'
        Sha256 = '233067de2af6d770895b7329975dc0c2b4379f4ae31123780f833a6c09acc5c3'
    },
    [ordered]@{
        Id = 'leatrix-maps'
        File = 'Leatrix_Maps-3.0.191.zip'
        Url = 'https://edge.forgecdn.net/files/5275/301/Leatrix_Maps-3.0.191.zip'
        Sha256 = 'ddcc5d5391df1362cd8dd7b18a1fc5d89185a7881d07828f63fbeb37ee9481a3'
    },
    [ordered]@{
        Id = 'nova-instance-tracker'
        File = 'NovaInstanceTracker-v1.55-Wrath.zip'
        Url = 'https://edge.forgecdn.net/files/5292/821/NovaInstanceTracker-v1.55-Wrath.zip'
        Sha256 = '3a5633148ff5d2c091b7ade195753bde5ef717bf59b0c1e1a62b8f0d4bf62165'
    },
    [ordered]@{
        Id = 'attune'
        File = 'Attune-WOTLK-314.zip'
        Url = 'https://edge.forgecdn.net/files/5758/185/Attune-WOTLK-314.zip'
        Sha256 = '855b68e4c1231df84edf940bbccbff4195e8c33cd5b7a6414a6e3f029fb8e861'
    },
    [ordered]@{
        Id = 'baganator'
        File = 'Baganator-158-wrath.zip'
        Url = 'https://edge.forgecdn.net/files/5112/311/Baganator-158-wrath.zip'
        Sha256 = '93a609a516c145bbce0283130bff0986a81a6114a3879a224fd524d1f29a33f1'
    }
)

$utf8Sources = @(
    [ordered]@{ File = 'UTF8.toc'; Sha256 = 'f52f7d287de2f2be3801a8754f868d353541cb55f91afde8f60289b637955de6' },
    [ordered]@{ File = 'lib.xml'; Sha256 = 'e7bccce6e32ad0f22497dda8fe709f045c8b302dce2f522e8b2684134726f4ec' },
    [ordered]@{ File = 'utf8.lua'; Sha256 = 'cf73054fde5eb968a649027c8056f513eb4c8b0249789dc35ee7e38dfc1f4668' },
    [ordered]@{ File = 'utf8data.lua'; Sha256 = '0c1c8c4c294c661c385c620666f4488e3cdbca40de70eedc931814b82c5f7cb8' }
)

if (Test-Path -LiteralPath $WorkDirectory) {
    Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}

$downloadDirectory = Join-Path $WorkDirectory 'downloads'
$extractDirectory = Join-Path $WorkDirectory 'extract'
$packageDirectory = Join-Path $WorkDirectory 'elvui-package'
$overlayDirectory = Join-Path $WorkDirectory 'elvui-libraries-overlay'
$dbmCorePackageDirectory = Join-Path $WorkDirectory 'dbm-core-package'
$publicPackagesDirectory = Join-Path $OutputDirectory 'packages'
New-Item -ItemType Directory -Path $downloadDirectory, $extractDirectory, $packageDirectory, $overlayDirectory, $dbmCorePackageDirectory, $publicPackagesDirectory -Force | Out-Null

foreach ($source in $sources) {
    Get-VerifiedFile $source.Url (Join-Path $downloadDirectory $source.File) $source.Sha256
}

$utf8DownloadDirectory = Join-Path $downloadDirectory 'utf8-r10'
New-Item -ItemType Directory -Path $utf8DownloadDirectory -Force | Out-Null
foreach ($source in $utf8Sources) {
    Get-VerifiedFile "https://repos.curseforge.com/wow/utf8/trunk/$($source.File)" (Join-Path $utf8DownloadDirectory $source.File) $source.Sha256
}

foreach ($source in $sources | Where-Object {
    $_.Id -in @('elvui-source', 'ace3', 'libdispel', 'libdualspec', 'libtranslit', 'dbm-core-source')
}) {
    Expand-Zip (Join-Path $downloadDirectory $source.File) (Join-Path $extractDirectory $source.Id)
}
Expand-Zip (Join-Path $downloadDirectory 'WeakAuras-5.12.8.zip') (Join-Path $extractDirectory 'weakauras')

foreach ($folder in @('DBM-Core', 'DBM-GUI', 'DBM-StatusBarTimers', 'DBM-VPVEM')) {
    Copy-Folder (Join-Path (Join-Path $extractDirectory 'dbm-core-source') $folder) (Join-Path $dbmCorePackageDirectory $folder)
}

$elvuiSource = Find-OnlyDirectory (Join-Path $extractDirectory 'elvui-source')
foreach ($folder in @('ElvUI', 'ElvUI_Libraries', 'ElvUI_Options')) {
    Copy-Folder (Join-Path $elvuiSource $folder) (Join-Path $packageDirectory $folder)
}

$coreLibraries = Join-Path $overlayDirectory 'ElvUI_Libraries\Core'
$ace3Source = Find-OnlyDirectory (Join-Path $extractDirectory 'ace3')
$ace3Target = Join-Path $coreLibraries 'Ace3'
foreach ($folder in @(
    'AceAddon-3.0',
    'AceComm-3.0',
    'AceConsole-3.0',
    'AceDB-3.0',
    'AceDBOptions-3.0',
    'AceEvent-3.0',
    'AceGUI-3.0',
    'AceHook-3.0',
    'AceSerializer-3.0',
    'AceTimer-3.0'
)) {
    Copy-Folder (Join-Path $ace3Source $folder) (Join-Path $ace3Target $folder)
}

Copy-Folder (Join-Path $ace3Source 'LibStub') (Join-Path $coreLibraries 'LibStub')
Copy-Folder (Join-Path $ace3Source 'CallbackHandler-1.0') (Join-Path $coreLibraries 'CallbackHandler-1.0')

$weakAurasLibraries = Join-Path $extractDirectory 'weakauras\WeakAuras\Libs'
$libraryMappings = @(
    @('AceGUI-3.0-SharedMediaWidgets', 'AceGUI-3.0-SharedMediaWidgets'),
    @('LibSharedMedia-3.0', 'LibSharedMedia-3.0'),
    @('TaintLess', 'TaintLess'),
    @('LibCustomGlow-1.0', 'LibCustomGlow-1.0'),
    @('LibDataBroker-1.1', 'LibDataBroker')
)
foreach ($mapping in $libraryMappings) {
    Copy-Folder (Join-Path $weakAurasLibraries $mapping[0]) (Join-Path $coreLibraries $mapping[1])
}

$libDispelSource = Find-OnlyDirectory (Join-Path $extractDirectory 'libdispel')
$libDualSpecSource = Find-OnlyDirectory (Join-Path $extractDirectory 'libdualspec')
$libTranslitSource = Find-OnlyDirectory (Join-Path $extractDirectory 'libtranslit')
Copy-Folder $libDispelSource (Join-Path $coreLibraries 'LibDispel')
Copy-Folder $libDualSpecSource (Join-Path $coreLibraries 'LibDualSpec-1.0')
Copy-Folder $libTranslitSource (Join-Path $coreLibraries 'LibTranslit-1.0')
Copy-Folder $utf8DownloadDirectory (Join-Path $coreLibraries 'UTF8')

$thirdPartyNotice = @"
Atlas WotLK ElvUI library overlay for client interface 30403.
This archive contains third-party libraries only. ElvUI itself is downloaded from its official v13.61 tag.
Source revisions and SHA-256 values are listed in SOURCES.json beside the public catalog.
"@
[System.IO.File]::WriteAllText(
    (Join-Path $coreLibraries 'Atlas-ThirdParty-Sources.txt'),
    $thirdPartyNotice,
    [System.Text.UTF8Encoding]::new($false))

Merge-Folder $overlayDirectory $packageDirectory

$textExtensions = @('.toc', '.lua', '.xml')
foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $_.Extension -in $textExtensions }) {
    $content = [System.IO.File]::ReadAllText($file.FullName)
    $updated = $content.Replace('@project-version@', '13.61').Replace('@project-date-iso@', '2024-04-03T01:55:08Z').Replace('@project-hash@', '24022eeb3e3622f06ffd2051b219bd9221e2ae3f')
    if ($updated -ne $content) {
        [System.IO.File]::WriteAllText($file.FullName, $updated, [System.Text.UTF8Encoding]::new($false))
    }
}

$packageTimestamp = [DateTime]::SpecifyKind([DateTime]::Parse('2024-04-03T01:55:08'), [DateTimeKind]::Utc)
foreach ($directory in @($packageDirectory, $overlayDirectory)) {
    Get-ChildItem -LiteralPath $directory -Recurse -Force | ForEach-Object {
        $_.LastWriteTimeUtc = $packageTimestamp
    }
}

$dbmPackageTimestamp = [DateTime]::SpecifyKind([DateTime]::Parse('2024-12-04T00:00:00'), [DateTimeKind]::Utc)
Get-ChildItem -LiteralPath $dbmCorePackageDirectory -Recurse -Force | ForEach-Object {
    $_.LastWriteTimeUtc = $dbmPackageTimestamp
}

$elvuiValidationArchive = Join-Path $WorkDirectory 'ElvUI-13.61-full-validation.zip'
$elvuiOverlayArchive = Join-Path $publicPackagesDirectory 'ElvUI-13.61-libraries-wotlk-30403.zip'
$dbmCoreArchive = Join-Path $publicPackagesDirectory 'DBM-Core-11.0.34-wotlk-30403.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $elvuiValidationArchive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
[System.IO.Compression.ZipFile]::CreateFromDirectory($overlayDirectory, $elvuiOverlayArchive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
[System.IO.Compression.ZipFile]::CreateFromDirectory($dbmCorePackageDirectory, $dbmCoreArchive, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$weakAurasArchive = Join-Path $downloadDirectory 'WeakAuras-5.12.8.zip'
$questieArchive = Join-Path $downloadDirectory 'Questie-v10.19.2.zip'
$elvuiSourceArchive = Join-Path $downloadDirectory 'v13.61.zip'
$dbmWotlkArchive = Join-Path $downloadDirectory 'DBM-Raids-WoTLK-r337.zip'
$dbmDungeonsArchive = Join-Path $downloadDirectory 'DBM-Party-WotLK-r122-wrath.zip'
$dbmLegacyArchive = Join-Path $downloadDirectory 'DBM-Vanilla_SoD_BC-r713.zip'
$detailsArchive = Join-Path $downloadDirectory 'Details-Details.20250119.13388.161.zip'
$atlasLootArchive = Join-Path $downloadDirectory 'AtlasLootClassic-v3.2.0.zip'
$auctionatorArchive = Join-Path $downloadDirectory 'Auctionator-10.2.0-wrath.zip'
$leatrixPlusArchive = Join-Path $downloadDirectory 'Leatrix_Plus-3.0.191.zip'
$leatrixMapsArchive = Join-Path $downloadDirectory 'Leatrix_Maps-3.0.191.zip'
$novaInstanceTrackerArchive = Join-Path $downloadDirectory 'NovaInstanceTracker-v1.55-Wrath.zip'
$attuneArchive = Join-Path $downloadDirectory 'Attune-WOTLK-314.zip'
$baganatorArchive = Join-Path $downloadDirectory 'Baganator-158-wrath.zip'

Assert-ZipRoots $weakAurasArchive @('WeakAuras', 'WeakAurasArchive', 'WeakAurasModelPaths', 'WeakAurasOptions', 'WeakAurasTemplates')
Assert-ZipRoots $questieArchive @('Questie')
Assert-ZipRoots $elvuiValidationArchive @('ElvUI', 'ElvUI_Libraries', 'ElvUI_Options')
Assert-ZipRoots $elvuiOverlayArchive @('ElvUI_Libraries')
Assert-ZipRoots $dbmCoreArchive @('DBM-Core', 'DBM-GUI', 'DBM-StatusBarTimers', 'DBM-VPVEM')
Assert-ZipRoots $dbmWotlkArchive @('DBM-Raids-WoTLK')
Assert-ZipRoots $dbmDungeonsArchive @('DBM-Party-BC', 'DBM-Party-Vanilla', 'DBM-Party-WotLK', 'DBM-WorldEvents')
Assert-ZipRoots $dbmLegacyArchive @('DBM-Azeroth', 'DBM-Outlands', 'DBM-Raids-BC', 'DBM-Raids-Vanilla')
Assert-ZipRoots $detailsArchive @('Details', 'Details_Compare2', 'Details_DataStorage', 'Details_EncounterDetails', 'Details_RaidCheck', 'Details_Streamer', 'Details_TinyThreat', 'Details_Vanguard')
Assert-ZipRoots $atlasLootArchive @('AtlasLootClassic', 'AtlasLootClassic_Collections', 'AtlasLootClassic_Crafting', 'AtlasLootClassic_Data', 'AtlasLootClassic_DungeonsAndRaids', 'AtlasLootClassic_Factions', 'AtlasLootClassic_Options', 'AtlasLootClassic_PvP')
Assert-ZipRoots $auctionatorArchive @('Auctionator')
Assert-ZipRoots $leatrixPlusArchive @('Leatrix_Plus')
Assert-ZipRoots $leatrixMapsArchive @('Leatrix_Maps')
Assert-ZipRoots $novaInstanceTrackerArchive @('NovaInstanceTracker')
Assert-ZipRoots $attuneArchive @('Attune')
Assert-ZipRoots $baganatorArchive @('Baganator')

$weakAurasSha256 = Get-Sha256 $weakAurasArchive
$questieSha256 = Get-Sha256 $questieArchive
$elvuiSourceSha256 = Get-Sha256 $elvuiSourceArchive
$elvuiOverlaySha256 = Get-Sha256 $elvuiOverlayArchive
$elvuiInstallHash = Get-StringSha256 "source=$elvuiSourceSha256;overlay=$elvuiOverlaySha256;version=13.61;date=2024-04-03T01:55:08Z;hash=24022eeb3e3622f06ffd2051b219bd9221e2ae3f"
$dbmCoreSha256 = Get-Sha256 $dbmCoreArchive
$dbmWotlkSha256 = Get-Sha256 $dbmWotlkArchive
$dbmDungeonsSha256 = Get-Sha256 $dbmDungeonsArchive
$dbmLegacySha256 = Get-Sha256 $dbmLegacyArchive
$dbmInstallHash = Get-StringSha256 "core=$dbmCoreSha256;wotlk=$dbmWotlkSha256;dungeons=$dbmDungeonsSha256;legacy=$dbmLegacySha256;version=11.0.34-r337-r122-r713"

$packageDefinitions = @(
    [ordered]@{
        id = 'weakauras'
        name = 'WeakAuras'
        description = [System.Text.RegularExpressions.Regex]::Unescape("Auras, alertes et \u00e9l\u00e9ments d'interface personnalisables.")
        version = '5.12.8'
        interface = '30403'
        archive = $weakAurasArchive
        url = 'https://github.com/WeakAuras/WeakAuras2/releases/download/5.12.8/WeakAuras-5.12.8.zip'
        installHash = $weakAurasSha256
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('WeakAuras', 'WeakAurasArchive', 'WeakAurasModelPaths', 'WeakAurasOptions', 'WeakAurasTemplates')
        sourceUrl = 'https://github.com/WeakAuras/WeakAuras2/releases/tag/5.12.8'
    },
    [ordered]@{
        id = 'elvui'
        name = 'ElvUI'
        description = "Remplacement complet et configurable de l'interface."
        version = '13.61'
        interface = '30403'
        archive = $elvuiSourceArchive
        url = 'https://github.com/tukui-org/ElvUI/archive/refs/tags/v13.61.zip'
        installHash = $elvuiInstallHash
        stripPrefix = 'ElvUI-13.61'
        components = @(
            [ordered]@{
                name = 'Bibliotheques WotLK Classic 30403'
                archive = $elvuiOverlayArchive
                url = "$($PublicBaseUrl.TrimEnd('/'))/$([System.IO.Path]::GetFileName($elvuiOverlayArchive))"
                stripPrefix = ''
            }
        )
        tokenReplacements = [ordered]@{
            '@project-version@' = '13.61'
            '@project-date-iso@' = '2024-04-03T01:55:08Z'
            '@project-hash@' = '24022eeb3e3622f06ffd2051b219bd9221e2ae3f'
        }
        folders = @('ElvUI', 'ElvUI_Libraries', 'ElvUI_Options')
        sourceUrl = 'https://github.com/tukui-org/ElvUI/tree/v13.61'
    },
    [ordered]@{
        id = 'questie'
        name = 'Questie'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Suivi des qu\u00eates, objectifs et marqueurs sur la carte.')
        version = '10.19.2'
        interface = '30403'
        archive = $questieArchive
        url = 'https://github.com/Questie/Questie/releases/download/v10.19.2/Questie-v10.19.2.zip'
        installHash = $questieSha256
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('Questie')
        sourceUrl = 'https://github.com/Questie/Questie/releases/tag/v10.19.2'
    },
    [ordered]@{
        id = 'dbm'
        name = 'Deadly Boss Mods (DBM)'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Alertes de boss pour les raids et donjons Vanilla, Burning Crusade et WotLK.')
        version = '11.0.34+r337+r122+r713'
        interface = '30403'
        archive = $dbmCoreArchive
        url = "$($PublicBaseUrl.TrimEnd('/'))/$([System.IO.Path]::GetFileName($dbmCoreArchive))"
        installHash = $dbmInstallHash
        stripPrefix = ''
        components = @(
            [ordered]@{
                name = 'Raids WotLK r337'
                archive = $dbmWotlkArchive
                url = 'https://edge.forgecdn.net/files/5763/677/DBM-Raids-WoTLK-r337.zip'
                stripPrefix = ''
            },
            [ordered]@{
                name = 'Donjons Vanilla, BC et WotLK r122'
                archive = $dbmDungeonsArchive
                url = 'https://edge.forgecdn.net/files/5194/699/DBM-Party-WotLK-r122-wrath.zip'
                stripPrefix = ''
            },
            [ordered]@{
                name = 'Raids Vanilla et BC r713'
                archive = $dbmLegacyArchive
                url = 'https://edge.forgecdn.net/files/5237/506/DBM-Vanilla_SoD_BC-r713.zip'
                stripPrefix = ''
            }
        )
        tokenReplacements = [ordered]@{}
        folders = @(
            'DBM-Core',
            'DBM-GUI',
            'DBM-StatusBarTimers',
            'DBM-VPVEM',
            'DBM-Raids-WoTLK',
            'DBM-Party-BC',
            'DBM-Party-Vanilla',
            'DBM-Party-WotLK',
            'DBM-WorldEvents',
            'DBM-Azeroth',
            'DBM-Outlands',
            'DBM-Raids-BC',
            'DBM-Raids-Vanilla'
        )
        sourceUrl = 'https://www.curseforge.com/wow/addons/deadly-boss-mods/files/5961449'
    },
    [ordered]@{
        id = 'details'
        name = 'Details!'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Mesure des d\u00e9g\u00e2ts, soins, menaces et statistiques de combat.')
        version = '20250119.13388.161'
        interface = '30403'
        archive = $detailsArchive
        url = 'https://edge.forgecdn.net/files/6102/986/Details-Details.20250119.13388.161.zip'
        installHash = Get-Sha256 $detailsArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('Details', 'Details_Compare2', 'Details_DataStorage', 'Details_EncounterDetails', 'Details_RaidCheck', 'Details_Streamer', 'Details_TinyThreat', 'Details_Vanguard')
        sourceUrl = 'https://www.curseforge.com/wow/addons/details/files/6102986'
    },
    [ordered]@{
        id = 'atlaslootclassic'
        name = 'AtlasLootClassic'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Catalogue des butins de donjons, raids, factions, JcJ et artisanat.')
        version = '3.2.0'
        interface = '30403'
        archive = $atlasLootArchive
        url = 'https://edge.forgecdn.net/files/4811/104/AtlasLootClassic-v3.2.0.zip'
        installHash = Get-Sha256 $atlasLootArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('AtlasLootClassic', 'AtlasLootClassic_Collections', 'AtlasLootClassic_Crafting', 'AtlasLootClassic_Data', 'AtlasLootClassic_DungeonsAndRaids', 'AtlasLootClassic_Factions', 'AtlasLootClassic_Options', 'AtlasLootClassic_PvP')
        sourceUrl = 'https://www.curseforge.com/wow/addons/atlaslootclassic/files/4811104'
    },
    [ordered]@{
        id = 'auctionator'
        name = 'Auctionator'
        description = [System.Text.RegularExpressions.Regex]::Unescape("Outils pratiques pour acheter, vendre et analyser l'h\u00f4tel des ventes.")
        version = '10.2.0-wrath'
        interface = '30403'
        archive = $auctionatorArchive
        url = 'https://edge.forgecdn.net/files/4869/540/Auctionator-10.2.0-wrath.zip'
        installHash = Get-Sha256 $auctionatorArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('Auctionator')
        sourceUrl = 'https://www.curseforge.com/wow/addons/auctionator/files/4869540'
    },
    [ordered]@{
        id = 'leatrix-maps'
        name = 'Leatrix Maps'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Carte du monde am\u00e9lior\u00e9e avec exploration, coordonn\u00e9es, redimensionnement et options de navigation.')
        version = '3.0.191'
        interface = '30403'
        archive = $leatrixMapsArchive
        url = 'https://edge.forgecdn.net/files/5275/301/Leatrix_Maps-3.0.191.zip'
        installHash = Get-Sha256 $leatrixMapsArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('Leatrix_Maps')
        sourceUrl = 'https://www.curseforge.com/wow/addons/leatrix-maps/files/5275301'
    },
    [ordered]@{
        id = 'leatrix-plus'
        name = 'Leatrix Plus'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Am\u00e9liorations de confort, automatisations et r\u00e9glages pratiques du client.')
        version = '3.0.191'
        interface = '30403'
        archive = $leatrixPlusArchive
        url = 'https://edge.forgecdn.net/files/5275/306/Leatrix_Plus-3.0.191.zip'
        installHash = Get-Sha256 $leatrixPlusArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('Leatrix_Plus')
        sourceUrl = 'https://www.curseforge.com/wow/addons/leatrix-plus-wrath/files/5275306'
    },
    [ordered]@{
        id = 'nova-instance-tracker'
        name = 'Nova Instance Tracker'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Suivi des entr\u00e9es, verrouillages et temps pass\u00e9 dans les instances.')
        version = '1.55-Wrath'
        interface = '30403'
        archive = $novaInstanceTrackerArchive
        url = 'https://edge.forgecdn.net/files/5292/821/NovaInstanceTracker-v1.55-Wrath.zip'
        installHash = Get-Sha256 $novaInstanceTrackerArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('NovaInstanceTracker')
        sourceUrl = 'https://www.curseforge.com/wow/addons/nova-instance-tracker/files/5292821'
    },
    [ordered]@{
        id = 'attune'
        name = 'Attune'
        description = [System.Text.RegularExpressions.Regex]::Unescape("Suivi des acc\u00e8s, pr\u00e9requis et progressions d'harmonisation.")
        version = 'WOTLK-314'
        interface = '30403'
        archive = $attuneArchive
        url = 'https://edge.forgecdn.net/files/5758/185/Attune-WOTLK-314.zip'
        installHash = Get-Sha256 $attuneArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('Attune')
        sourceUrl = 'https://www.curseforge.com/wow/addons/attune/files/5758185'
    },
    [ordered]@{
        id = 'baganator'
        name = 'Baganator'
        description = [System.Text.RegularExpressions.Regex]::Unescape('Sacs et banque unifi\u00e9s avec cat\u00e9gories, recherche, tri et inventaire des personnages.')
        version = '158-wrath'
        interface = '30403'
        archive = $baganatorArchive
        url = 'https://edge.forgecdn.net/files/5112/311/Baganator-158-wrath.zip'
        installHash = Get-Sha256 $baganatorArchive
        stripPrefix = ''
        components = @()
        tokenReplacements = [ordered]@{}
        folders = @('Baganator')
        sourceUrl = 'https://www.curseforge.com/wow/addons/baganator/files/5112311'
    }
)

$addonCategories = @{
    'weakauras' = 'Combat'
    'elvui' = 'Interface'
    'questie' = 'Quêtes'
    'dbm' = 'Combat'
    'details' = 'Combat'
    'atlaslootclassic' = 'Collections'
    'auctionator' = 'Économie'
    'leatrix-maps' = 'Interface'
    'leatrix-plus' = 'Interface'
    'nova-instance-tracker' = 'Instances'
    'attune' = 'Quêtes'
    'baganator' = 'Inventaire'
}

$catalogAddons = foreach ($package in $packageDefinitions) {
    $archiveItem = Get-Item -LiteralPath $package.archive
    $catalogComponents = foreach ($component in $package.components) {
        $componentItem = Get-Item -LiteralPath $component.archive
        [ordered]@{
            name = $component.name
            url = $component.url
            size = $componentItem.Length
            sha256 = Get-Sha256 $componentItem.FullName
            stripPrefix = $component.stripPrefix
        }
    }
    [ordered]@{
        id = $package.id
        name = $package.name
        description = $package.description
        category = $addonCategories[$package.id]
        version = $package.version
        interface = $package.interface
        url = $package.url
        size = $archiveItem.Length
        sha256 = Get-Sha256 $archiveItem.FullName
        installHash = $package.installHash
        stripPrefix = $package.stripPrefix
        components = @($catalogComponents)
        tokenReplacements = $package.tokenReplacements
        folders = $package.folders
        sourceUrl = $package.sourceUrl
    }
}

$catalog = [ordered]@{
    schemaVersion = 1
    clientInterface = '30403'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    addons = @($catalogAddons)
}

[System.IO.File]::WriteAllText(
    (Join-Path $OutputDirectory 'catalog.json'),
    ($catalog | ConvertTo-Json -Depth 10).Replace("`r`n", "`n"),
    [System.Text.UTF8Encoding]::new($false))

$sourceAudit = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sources = @($sources + $utf8Sources)
}
[System.IO.File]::WriteAllText(
    (Join-Path $OutputDirectory 'SOURCES.json'),
    ($sourceAudit | ConvertTo-Json -Depth 10).Replace("`r`n", "`n"),
    [System.Text.UTF8Encoding]::new($false))

Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File |
    Select-Object Name, Length, @{ Name = 'Sha256'; Expression = { Get-Sha256 $_.FullName } }
