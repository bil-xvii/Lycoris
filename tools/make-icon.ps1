# Generates src/Monolith/Assets/monolith.ico (+ PNG) — the MONOLITH logo:
# a white slab with a cut corner on a black square.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Monolith\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::FromArgb(255, 10, 10, 10))

    # Slab: 1 : 2.4 proportion, centered, top-right corner cut at 45 degrees
    $w = [math]::Max(3, [math]::Round($size * 0.30))
    $h = [math]::Round($size * 0.72)
    $x = [math]::Round(($size - $w) / 2)
    $y = [math]::Round(($size - $h) / 2)
    $cut = [math]::Max(1, [math]::Round($w * 0.45))
    $pts = @(
        (New-Object System.Drawing.PointF $x, $y),
        (New-Object System.Drawing.PointF ($x + $w - $cut), $y),
        (New-Object System.Drawing.PointF ($x + $w), ($y + $cut)),
        (New-Object System.Drawing.PointF ($x + $w), ($y + $h)),
        (New-Object System.Drawing.PointF $x, ($y + $h))
    )
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 245, 245))
    $g.FillPolygon($brush, [System.Drawing.PointF[]]$pts)

    # Base line under the slab (only on larger sizes)
    if ($size -ge 32) {
        $lineH = [math]::Max(1, [math]::Round($size * 0.025))
        $g.FillRectangle($brush, [math]::Round($size * 0.18), $y + $h + [math]::Round($size * 0.05), [math]::Round($size * 0.64), $lineH)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-LogoBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($s, $ms.ToArray())
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'monolith-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($s -eq 64) { $bmp.Save((Join-Path $OutDir 'monolith-64.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO container with PNG-compressed entries
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'monolith.ico'), $out.ToArray())
"icon written to $OutDir"
