[CmdletBinding()]
param()

# Windows-only format conversion. Visual edits belong in the versioned PNG master.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repository = Split-Path -Parent $PSScriptRoot
$masterPath = Join-Path $repository 'design\atlas-branding\atlas-app-icon-v1.png'
$master = [Drawing.Bitmap]::new($masterPath)

function ConvertTo-IconPng([Drawing.Image]$Source, [int]$Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.DrawImage($Source, [Drawing.Rectangle]::new(0, 0, $Size, $Size))
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    if ($master.Width -ne $master.Height -or $master.GetPixel(0, 0).A -ne 0) {
        throw 'The icon master must be square with transparent corners.'
    }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = foreach ($size in $sizes) {
        [pscustomobject]@{ Size = $size; Bytes = (ConvertTo-IconPng $master $size) }
    }
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0) # Reserved
        $writer.Write([uint16]1) # ICO
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0) # True color
            $writer.Write([byte]0)
            $writer.Write([uint16]1) # Planes
            $writer.Write([uint16]32) # RGBA
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
        $writer.Flush()
        $icoBytes = $stream.ToArray()
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
    $pngBytes = ConvertTo-IconPng $master 512
    foreach ($project in @('WotLK.Launcher', 'WotLK.Launcher.Installer')) {
        $assets = Join-Path $repository "source\$project\Assets"
        [IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $icoBytes)
        [IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.png'), $pngBytes)
    }
    [IO.File]::WriteAllBytes((Join-Path $repository 'source\WotLK.Launcher\Assets\Branding\AtlasLauncherLogo.png'), $pngBytes)
    Write-Output "Atlas icons generated: PNG 512px; ICO $($sizes -join ', ')px (32-bit alpha)."
} finally {
    $master.Dispose()
}
