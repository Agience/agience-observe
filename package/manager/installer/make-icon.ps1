# make-icon.ps1 — render agience.ico from the brand mark.
#
# The one icon the Start menu shortcut, the window title bar and Add/Remove Programs all use. The
# TRAY icon is a different thing and is drawn in `Icons.cs` at run time: it is this mark with a
# status badge composited over it, because a tray icon has to say what the services are doing and a
# logo alone cannot.
#
# ⛔ THE SOURCE PNG IS COMMITTED, THE .ico IS NOT. `agience-logo.png` came from outside this
# repository, and a build that reached into one workstation's Development folder for its brand mark
# would work on exactly one machine. The .ico is derived from it on every build.
#
# ⚠ FOUR SIZES IN ONE FILE, AND THE 256 IS PNG-COMPRESSED. Windows picks per surface — 16 in the
# title bar, 32 in the Start menu, 48 in Explorer, 256 in the large-icon view — and an .ico carrying
# only one size is RESCALED into the others, which is what makes an installer look homemade. The
# 256 must be PNG rather than BMP: a 256x256 BMP entry is 256 KB and some shells reject it.
[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\agience-logo.png",
    [string]$Out = "$PSScriptRoot\agience.ico"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "the brand mark is missing: $Source" }

$sizes = @(16, 32, 48, 256)
$pngs = @()
$logo = [System.Drawing.Image]::FromFile($Source)

try {
    foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            # HighQualityBicubic, because the mark is 1407px of thin interlaced strokes going down
            # to 16. Anything cheaper turns those strokes into aliased confetti at the small sizes,
            # which is precisely where an icon is judged.
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)

            # ⚠ NO PADDING. The source is already cropped to the mark, and Windows expects an app
            # icon to fill its box — a mark inset inside its own icon reads as smaller than every
            # other icon in the Start menu.
            $g.DrawImage($logo, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
        }
        finally {
            $g.Dispose()
        }

        $stream = New-Object System.IO.MemoryStream
        $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngs += , $stream.ToArray()
        $stream.Dispose()
    }
}
finally {
    $logo.Dispose()
}

# ── the ICONDIR, written by hand ────────────────────────────────────────────────────────────────
# System.Drawing can READ an .ico and cannot write a multi-size one, so the container is assembled
# here: 6 bytes of header, 16 per entry, then the images.
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($fs)
try {
    $w.Write([UInt16]0)               # reserved
    $w.Write([UInt16]1)               # type: icon
    $w.Write([UInt16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        # ⚠ 256 IS WRITTEN AS 0. The width and height fields are ONE BYTE, so 256 does not fit; zero
        # is the agreed spelling for it. Writing 255 gives a 255-pixel icon nothing asks for.
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([Byte]$dim)          # width
        $w.Write([Byte]$dim)          # height
        $w.Write([Byte]0)             # palette entries: 0 = truecolour
        $w.Write([Byte]0)             # reserved
        $w.Write([UInt16]1)           # colour planes
        $w.Write([UInt16]32)          # bits per pixel
        $w.Write([UInt32]$pngs[$i].Length)
        $w.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }

    foreach ($png in $pngs) { $w.Write($png) }
}
finally {
    $w.Dispose()
    $fs.Dispose()
}

Write-Host "wrote $Out ($((Get-Item $Out).Length) bytes, sizes: $($sizes -join ', ')) from $(Split-Path $Source -Leaf)"
