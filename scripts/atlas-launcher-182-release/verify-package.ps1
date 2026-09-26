$ErrorActionPreference = 'Stop'
$atlasRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$atlasOutput = Join-Path $atlasRepo 'artifacts/atlas-release-182'
$atlasAssembly = [Reflection.Assembly]::LoadFile((Join-Path $atlasRepo 'source/WotLK.Launcher/bin/Release/net8.0-windows10.0.17763.0/win-x64/WotLK.Launcher.dll'))
$atlasFlavor = $atlasAssembly.GetType('WotLK.Launcher.LauncherBuildFlavor', $true)
$atlasFlags = [Reflection.BindingFlags]'NonPublic,Static'
if ($atlasFlavor.GetField('IsLocalClient', $atlasFlags).GetRawConstantValue()) { throw 'Local build cannot be published.' }
if (-not $atlasFlavor.GetField('IsSelfUpdateEnabled', $atlasFlags).GetRawConstantValue()) { throw 'Self update disabled.' }
if ($atlasFlavor.GetField('SettingsDirectoryName', $atlasFlags).GetRawConstantValue() -ne 'WotLK Launcher') { throw 'Settings continuity broken.' }
$atlasResources = @{
    'assets/appicon.ico' = 'source/WotLK.Launcher/Assets/AppIcon.ico'
    'assets/appicon.png' = 'source/WotLK.Launcher/Assets/AppIcon.png'
    'assets/branding/atlaslauncherlogo.png' = 'source/WotLK.Launcher/Assets/Branding/AtlasLauncherLogo.png'
    'assets/launcher/visuals/atlas-auth-background.png' = 'design/atlas-branding/atlas-launcher-background-v7.png'
}
$atlasVerifiedResources = @{}
$atlasReader = [Resources.ResourceReader]::new($atlasAssembly.GetManifestResourceStream('WotLK.Launcher.g.resources'))
try {
    $atlasEnumerator = $atlasReader.GetEnumerator()
    while ($atlasEnumerator.MoveNext()) {
        if ($atlasResources.ContainsKey($atlasEnumerator.Key)) {
            $atlasHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.Stream]$atlasEnumerator.Value))
            if ($atlasHash -ne (Get-FileHash -LiteralPath (Join-Path $atlasRepo $atlasResources[$atlasEnumerator.Key])).Hash) { throw 'Resource differs.' }
            $atlasVerifiedResources[$atlasEnumerator.Key] = $atlasHash.ToLowerInvariant()
        }
    }
} finally { $atlasReader.Dispose() }
if ($atlasVerifiedResources.Count -ne 4) { throw 'Missing branding resource.' }
$atlasArmory = $atlasAssembly.GetManifestResourceStream('Atlas.Armory.Runtime.zip')
try { $atlasArmoryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($atlasArmory)).ToLowerInvariant() }
finally { $atlasArmory.Dispose() }
if ($atlasArmoryHash -ne '84a57db71c985be18c47f62e5761e21effe7d032a7316e0f69e9edc650a7e645') { throw 'Armory payload differs.' }
$atlasPackages = @()
foreach ($atlasRelative in @('client/WotLK-Launcher.exe', 'installer/AtlasLauncherSetup.exe')) {
    $atlasFile = Join-Path $atlasOutput $atlasRelative
    $atlasInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($atlasFile)
    if ($atlasInfo.FileVersion -ne '1.8.2.0' -or $atlasInfo.ProductVersion -ne '1.8.2' -or $atlasInfo.CompanyName -ne 'Atlas') { throw 'Package metadata differs.' }
    $atlasPackages += [ordered]@{name=[IO.Path]::GetFileName($atlasFile); bytes=(Get-Item -LiteralPath $atlasFile).Length; sha256=(Get-FileHash -LiteralPath $atlasFile).Hash.ToLowerInvariant()}
}
$atlasProof = [ordered]@{version='1.8.2'; publicBuild=$true; selfUpdateEnabled=$true; settingsDirectory='WotLK Launcher'; resourceHashes=$atlasVerifiedResources; armorySha256=$atlasArmoryHash; packages=$atlasPackages; launcherStarted=$false; localClientReplaced=$false}
$atlasProof | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $atlasOutput 'package-verification.json') -Encoding utf8
$atlasProof | ConvertTo-Json -Depth 6
