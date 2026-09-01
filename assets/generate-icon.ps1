# Generates the Datameter logo: assets/datameter.ico and assets/logo-256.png
#
# The mark is a signal arc whose three bands are different colours — one signal made of
# several networks, which is the whole point of the app. The palette is the app's own network
# palette, so the icon and the contribution bar agree.
#
# Run:  powershell -File assets\generate-icon.ps1

Add-Type -AssemblyName System.Drawing

$OutDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Master = 512    # drawn once at high resolution, then downscaled for each icon size

function New-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # --- tile -------------------------------------------------------------
    $radius = $size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc(($size - $d), 0, $d, $d, 270, 90)
    $path.AddArc(($size - $d), ($size - $d), $d, $d, 0, 90)
    $path.AddArc(0, ($size - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $tile = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 24, 30, 38),
        [System.Drawing.Color]::FromArgb(255, 13, 17, 22))
    $g.FillPath($tile, $path)

    # --- signal arcs ------------------------------------------------------
    # Centred on the dot near the bottom, opening upward. Inner band first.
    $cx = $size * 0.5
    $cy = $size * 0.735
    $stroke = $size * 0.082

    $bands = @(
        @{ R = $size * 0.16; Color = [System.Drawing.Color]::FromArgb(255, 76, 194, 255) },   # cyan
        @{ R = $size * 0.28; Color = [System.Drawing.Color]::FromArgb(255, 92, 214, 169) },   # green
        @{ R = $size * 0.40; Color = [System.Drawing.Color]::FromArgb(255, 255, 169, 77) }    # amber
    )

    foreach ($b in $bands) {
        $pen = New-Object System.Drawing.Pen($b.Color, $stroke)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $r = $b.R
        $g.DrawArc($pen, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2), 208, 124)
        $pen.Dispose()
    }

    # --- the dot ----------------------------------------------------------
    $dot = $size * 0.055
    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 76, 194, 255))
    $g.FillEllipse($dotBrush, ($cx - $dot), ($cy - $dot), ($dot * 2), ($dot * 2))
    $dotBrush.Dispose()

    $g.Dispose(); $tile.Dispose(); $path.Dispose()
    return $bmp
}

function Resize-Bitmap($source, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    # The comma keeps PowerShell from unrolling the byte[] into the pipeline, which would
    # hand the caller an object[] that BinaryWriter will not accept.
    return ,$bytes
}

# --- render -----------------------------------------------------------------
$master = New-Logo $Master

$pngPath = Join-Path $OutDir 'logo-256.png'
$preview = Resize-Bitmap $master 256
$preview.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Output ("wrote {0}" -f $pngPath)

# --- pack the .ico ----------------------------------------------------------
# Vista and later accept PNG-compressed entries at every size, which keeps the file small
# and the alpha channel clean.
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $scaled = Resize-Bitmap $master $s
    $bytes = Get-PngBytes $scaled
    $scaled.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $bytes }
}

$icoPath = Join-Path $OutDir 'datameter.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }   # 0 means 256 in an ICONDIRENTRY
    $bw.Write([byte]$dim)            # width
    $bw.Write([byte]$dim)            # height
    $bw.Write([byte]0)               # palette size
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # colour planes
    $bw.Write([uint16]32)            # bits per pixel
    $bw.Write([uint32]$img.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img.Bytes, 0, $img.Bytes.Length) }

$bw.Dispose(); $fs.Dispose()
$master.Dispose()

Write-Output ("wrote {0} ({1} sizes, {2:N1} KB)" -f $icoPath, $images.Count, ((Get-Item $icoPath).Length / 1KB))
