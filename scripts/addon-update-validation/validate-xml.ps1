param([Parameter(Mandatory=$true)][string]$StageRoot)
$ErrorActionPreference = 'Stop'
$addonXmlStage = (Resolve-Path -LiteralPath $StageRoot).Path
$addonXmlSettings = [System.Xml.XmlReaderSettings]::new()
$addonXmlSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
$addonXmlSettings.XmlResolver = $null
$addonXmlErrors = @()
$addonXmlSnippets = @()
$addonXmlFiles = @(Get-ChildItem -LiteralPath (Join-Path $addonXmlStage 'extracted') -Recurse -File -Filter '*.xml')
foreach ($addonXmlFile in $addonXmlFiles) {
    $addonXmlReader = $null
    try {
        $addonXmlReader = [System.Xml.XmlReader]::Create($addonXmlFile.FullName, $addonXmlSettings)
        $addonXmlDocument = [System.Xml.XmlDocument]::new()
        $addonXmlDocument.XmlResolver = $null
        $addonXmlDocument.Load($addonXmlReader)
        foreach ($addonXmlNode in $addonXmlDocument.SelectNodes('//*')) {
            if (($addonXmlNode.LocalName -match '^On[A-Z]') -or ($addonXmlNode.LocalName -eq 'Script' -and -not $addonXmlNode.HasAttribute('file'))) {
                if ($addonXmlNode.InnerText.Trim()) {
                    $addonXmlSnippets += [pscustomobject]@{path=$addonXmlFile.FullName;node=$addonXmlNode.LocalName;code=$addonXmlNode.InnerText}
                }
            }
        }
    } catch {
        $addonXmlErrors += [pscustomobject]@{path=$addonXmlFile.FullName;error=$_.Exception.Message}
    } finally {
        if ($addonXmlReader) { $addonXmlReader.Dispose() }
    }
}
[pscustomobject]@{xmlCount=$addonXmlFiles.Count;errors=$addonXmlErrors;inlineLua=$addonXmlSnippets} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $addonXmlStage 'xml-validation.json') -Encoding utf8
[pscustomobject]@{xmlCount=$addonXmlFiles.Count;errors=$addonXmlErrors.Count;inlineLua=$addonXmlSnippets.Count} | ConvertTo-Json -Compress
if ($addonXmlErrors.Count) { exit 1 }
