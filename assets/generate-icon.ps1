# Generates the Datameter logo: assets/datameter.ico and assets/logo-256.png
#
# The mark is a gauge: a 216° band in the app's three network colours, cut into three zones
# by two slots, with a needle reading in the green. The band is the contribution bar bent
# into a dial; the zones are networks, largest first; and the needle sits in the green rather
# than the amber so that the notification area shows a meter at rest, not a standing warning.
#
# Every edge is somewhere on purpose. The ends of the band are flat radial cuts. The slots are
# not: a gap cut on two radial lines is a wedge, wider at the rim than at the inside, so each
# zone is drawn as a filled sector whose slot face is a straight line offset half the slot
# width from the slot's centre line. Both faces of a slot are then parallel, and both slots
# are identical. That face meets a circle of radius r at an angular offset of asin(d / r) from
# the centre line, which is the only trigonometry in here.
#
# Sizes are drawn individually rather than downsampled from one master, so the small ones can
# differ where they need to: 16 px drops the tile and fills the square with the glyph, because
# a tile that size leaves a band under two pixels wide, and 16 and 24 widen the slots and the
# needle slightly so they survive at under a pixel. The .ico holds one image per size and
# Windows picks the nearest, so the tray gets the 16 and Start gets the 256.
#
# Run:  powershell -File assets\generate-icon.ps1

Add-Type -AssemblyName System.Drawing

$OutDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- the design, in a 256-unit box ------------------------------------------------------
# Angles are screen angles: 0° at three o'clock, increasing clockwise, which is how GDI+ and
# SVG both count. The same numbers draw the SVG sources in the design notes.
$Design = @{
    Centre      = @{ X = 128.0; Y = 156.0 }
    OuterRadius = 89.0                  # band centreline 74, width 30
    InnerRadius = 59.0
    StartAngle  = 162.0                 # flat radial ends at 162° and 18°
    EndAngle    = 378.0
    Slots       = @(261.0, 336.0)       # centre lines of the two slots
    SlotWidth   = 6.0
    NeedleAngle = 298.5                 # the middle of the green zone
    NeedleReach = 74.0                  # from the centre to the band's centreline
    NeedleWidth = 14.0
    HubRadius   = 15.0
    TileRadius  = 56.0                  # 22% of the box, as before
}

$Cyan  = [System.Drawing.Color]::FromArgb(255,  76, 194, 255)
$Green = [System.Drawing.Color]::FromArgb(255,  92, 214, 169)
$Amber = [System.Drawing.Color]::FromArgb(255, 255, 169,  77)
$Ink   = [System.Drawing.Color]::FromArgb(255, 242, 242, 242)
$TileTop    = [System.Drawing.Color]::FromArgb(255, 24, 30, 38)
$TileBottom = [System.Drawing.Color]::FromArgb(255, 13, 17, 22)

# Sizes at or below this draw with wider slots and a heavier needle, or both vanish.
$SmallLimit      = 24
$SmallSlotWidth  = 9.0
$SmallNeedleWidth = 16.0

# Sizes that drop the tile and let the glyph fill the square. The bare glyph occupies the
# 200-unit box from (28, 28) to (228, 228) of the design.
$BareSizes = @(16)
$BareInset = 28.0
$BareBox   = 200.0

function Get-Zones([double]$slotWidth) {
    # Each zone is a sector: an outer arc, a straight slot face, an inner arc back, a straight
    # face home. The faces are parallel to the slot's centre line, so the arcs stop short of it
    # by asin(d / r), which is larger on the inner circle than the outer one.
    $d  = $slotWidth / 2
    $po = [math]::Asin($d / $Design.OuterRadius) * 180 / [math]::PI
    $pi = [math]::Asin($d / $Design.InnerRadius) * 180 / [math]::PI
    $s1 = $Design.Slots[0]
    $s2 = $Design.Slots[1]

    @(
        @{ Color = $Cyan;  OuterStart = $Design.StartAngle; OuterEnd = $s1 - $po; InnerStart = $Design.StartAngle; InnerEnd = $s1 - $pi }
        @{ Color = $Green; OuterStart = $s1 + $po;          OuterEnd = $s2 - $po; InnerStart = $s1 + $pi;          InnerEnd = $s2 - $pi }
        @{ Color = $Amber; OuterStart = $s2 + $po;          OuterEnd = $Design.EndAngle; InnerStart = $s2 + $pi;   InnerEnd = $Design.EndAngle }
    )
}

function Draw-Glyph($g, [double]$slotWidth, [double]$needleWidth) {
    # Drawn in design units; the caller has set the transform.
    $cx = $Design.Centre.X
    $cy = $Design.Centre.Y
    $ro = $Design.OuterRadius
    $ri = $Design.InnerRadius

    foreach ($z in Get-Zones $slotWidth) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc([single]($cx - $ro), [single]($cy - $ro), [single](2 * $ro), [single](2 * $ro),
                     [single]$z.OuterStart, [single]($z.OuterEnd - $z.OuterStart))
        # Back along the inner circle. AddArc joins it to the previous arc with a straight
        # line, and that line is the slot face.
        $path.AddArc([single]($cx - $ri), [single]($cy - $ri), [single](2 * $ri), [single](2 * $ri),
                     [single]$z.InnerEnd, [single]($z.InnerStart - $z.InnerEnd))
        $path.CloseFigure()

        $brush = New-Object System.Drawing.SolidBrush($z.Color)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }

    # The needle, then the hub over its root.
    $a = $Design.NeedleAngle * [math]::PI / 180
    $tipX = $cx + $Design.NeedleReach * [math]::Cos($a)
    $tipY = $cy + $Design.NeedleReach * [math]::Sin($a)

    $pen = New-Object System.Drawing.Pen($Ink, [single]$needleWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, [single]$cx, [single]$cy, [single]$tipX, [single]$tipY)
    $pen.Dispose()

    $hub = New-Object System.Drawing.SolidBrush($Ink)
    $r = $Design.HubRadius
    $g.FillEllipse($hub, [single]($cx - $r), [single]($cy - $r), [single](2 * $r), [single](2 * $r))
    $hub.Dispose()
}

function Draw-Tile($g, [int]$px) {
    # In pixels, before any transform, so the gradient runs corner to corner of the bitmap.
    $radius = $px * ($Design.TileRadius / 256)
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc(($px - $d), 0, $d, $d, 270, 90)
    $path.AddArc(($px - $d), ($px - $d), $d, $d, 0, 90)
    $path.AddArc(0, ($px - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $tile = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($px, $px)),
        $TileTop, $TileBottom)
    $g.FillPath($tile, $path)
    $tile.Dispose(); $path.Dispose()
}

function New-Logo([int]$size) {
    # Rendered at several times the target and reduced, which gives cleaner edges than GDI+'s
    # own anti-aliasing on shapes this small.
    $factor = if ($size -ge 128) { 2 } else { 4 }
    $px = $size * $factor
    $bare = $BareSizes -contains $size
    $small = $size -le $SmallLimit

    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    if (-not $bare) { Draw-Tile $g $px }

    # Map design units onto the bitmap: the whole 256 box for a tile, the 200 box the glyph
    # occupies when there is no tile. Prepend order, so points are offset before being scaled.
    if ($bare) {
        $scale = $px / $BareBox
        $g.ScaleTransform($scale, $scale)
        $g.TranslateTransform(-$BareInset, -$BareInset)
    } else {
        $scale = $px / 256
        $g.ScaleTransform($scale, $scale)
    }

    $slot = if ($small) { $SmallSlotWidth } else { $Design.SlotWidth }
    $needle = if ($small) { $SmallNeedleWidth } else { $Design.NeedleWidth }
    Draw-Glyph $g $slot $needle
    $g.Dispose()

    $out = Resize-Bitmap $bmp $size
    $bmp.Dispose()
    return $out
}

function Resize-Bitmap($source, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    # Without this the resampler reads past the source's edge as transparent black and leaves
    # a dark fringe along the outside of the tile.
    $attrs = New-Object System.Drawing.Imaging.ImageAttributes
    $attrs.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $dest = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $g.DrawImage($source, $dest, 0, 0, $source.Width, $source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attrs)
    $attrs.Dispose(); $g.Dispose()
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
$pngPath = Join-Path $OutDir 'logo-256.png'
$preview = New-Logo 256
$preview.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Output ("wrote {0}" -f $pngPath)

# --- pack the .ico ----------------------------------------------------------
# Vista and later accept PNG-compressed entries at every size, which keeps the file small
# and the alpha channel clean.
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $img = New-Logo $s
    $bytes = Get-PngBytes $img
    $img.Dispose()
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

Write-Output ("wrote {0} ({1} sizes, {2:N1} KB)" -f $icoPath, $images.Count, ((Get-Item $icoPath).Length / 1KB))
