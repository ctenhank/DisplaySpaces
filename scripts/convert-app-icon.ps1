[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetDirectory = Join-Path $repositoryRoot 'src\BetterVirtualDesktop.App\Assets'
$sourcePath = Join-Path $assetDirectory 'BetterVirtualDesktop.png'
$outputPath = Join-Path $assetDirectory 'BetterVirtualDesktop.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()

# Package the source artwork as a PNG-compressed Windows icon with real frames
# for common taskbar, tray, Explorer and high-DPI sizes. Preserve its alpha.
$source = [System.Drawing.Image]::FromFile($sourcePath)
try {
    if ($source.Width -ne $source.Height) { throw 'The app icon artwork must be square.' }
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
        $buffer = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $destination = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
            $graphics.DrawImage($source, $destination, 0, 0, $source.Width, $source.Height,
                [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($buffer.ToArray())
        }
        finally {
            $buffer.Dispose()
            $attributes.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally { $source.Dispose() }

$output = [System.IO.File]::Create($outputPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally {
    $writer.Dispose()
    $output.Dispose()
}
Write-Host "App icon: $outputPath ($($sizes -join ', ') px)"
