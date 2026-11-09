#requires -Version 5
# Generates a multi-size .ico from a source PNG.
# Frames are encoded as PNG inside the ICO (supported by Windows Vista+).
param(
    [string]$Source = "$PSScriptRoot/../src/AresToys.App/Assets/AresToysLogo.png",
    [string]$Output = "$PSScriptRoot/../src/AresToys.App/Assets/icon.ico",
    [int[]]$Sizes  = @(16, 24, 32, 48, 64, 128, 256)
)

Add-Type -AssemblyName System.Drawing

$srcAbs = (Resolve-Path $Source).Path
$outAbs = [System.IO.Path]::GetFullPath($Output)

$src = [System.Drawing.Image]::FromFile($srcAbs)
try {
    # Build the resized PNG byte arrays
    $frames = foreach ($size in $Sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CompositingQuality   = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.InterpolationMode    = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode        = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode      = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
        } finally {
            $g.Dispose()
        }

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()

        [pscustomobject]@{
            Size  = $size
            Bytes = $ms.ToArray()
        }
    }

    # ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes) per frame
    $headerSize = 6 + (16 * $frames.Count)
    $offset = $headerSize

    $stream = [System.IO.File]::Open($outAbs, 'Create')
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        # ICONDIR
        $writer.Write([uint16]0)        # reserved
        $writer.Write([uint16]1)        # type = 1 (icon)
        $writer.Write([uint16]$frames.Count)

        foreach ($f in $frames) {
            $w = if ($f.Size -ge 256) { 0 } else { $f.Size }
            $h = if ($f.Size -ge 256) { 0 } else { $f.Size }

            $writer.Write([byte]$w)               # width  (0 = 256)
            $writer.Write([byte]$h)               # height (0 = 256)
            $writer.Write([byte]0)                # color count (0 = >= 256 colors)
            $writer.Write([byte]0)                # reserved
            $writer.Write([uint16]1)              # planes
            $writer.Write([uint16]32)             # bit count
            $writer.Write([uint32]$f.Bytes.Length)# bytes in resource
            $writer.Write([uint32]$offset)        # offset

            $offset += $f.Bytes.Length
        }

        foreach ($f in $frames) {
            $writer.Write($f.Bytes)
        }
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    Write-Host "Wrote $outAbs ($($frames.Count) frames: $($Sizes -join ', '))"
} finally {
    $src.Dispose()
}
