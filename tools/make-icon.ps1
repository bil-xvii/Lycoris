# Lycoris icon generator (spider lily, Lycoris radiata).
# Usage:  tools\make-icon.ps1 -Variant A|B|All     (default All)
#   A = white flower on near-black tile (monochrome, matches the app)
#   B = deep-red flower on near-black tile
# Writes into docs/branding: lycoris-a.ico / lycoris-b.ico (16..256, PNG entries),
# lycoris-a-256.png / lycoris-b-256.png; with All also lycoris-mark-white.svg/.png
# (transparent) and icon-preview.png. Deterministic. Windows PowerShell 5.1 compatible.
# File is UTF-8 with BOM (Cyrillic labels in the preview sheet).
param(
    [ValidateSet('A', 'B', 'All')][string]$Variant = 'All',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\docs\branding')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;

public static class Lycoris
{
    public class Dot { public float X, Y, R; }
    public class Shape
    {
        internal List<PointF[]> Polys = new List<PointF[]>();
        internal List<Dot> Dots = new List<Dot>();
    }

    // Tapered stroke along a curve whose heading (deg from vertical, + = right) eases from hs to he.
    static void Stroke(Shape s, float ox, float oy, float len, float hs, float he, float pw,
                       float w0, float wMid, float wTip, float dotR, float midAt)
    {
        const int N = 56;
        var c = new PointF[N + 1]; var h = new float[N + 1];
        float x = ox, y = oy; c[0] = new PointF(x, y);
        for (int i = 0; i <= N; i++)
        {
            float t = (float)i / N;
            h[i] = hs + (he - hs) * (float)Math.Pow(t, pw);
            if (i > 0)
            {
                double r = h[i] * Math.PI / 180.0;
                x += (float)Math.Sin(r) * len / N; y -= (float)Math.Cos(r) * len / N;
                c[i] = new PointF(x, y);
            }
        }
        var left = new PointF[N + 1]; var right = new PointF[N + 1];
        for (int i = 0; i <= N; i++)
        {
            float t = (float)i / N;
            float w;
            if (wMid > 0) // lens-like petal: grows to wMid at midAt then tapers
                w = t < midAt ? w0 + (wMid - w0) * (float)Math.Sin(t / midAt * Math.PI / 2)
                              : wTip + (wMid - wTip) * (float)Math.Pow(Math.Cos((t - midAt) / (1 - midAt) * Math.PI / 2), 1.2);
            else
                w = wTip + (w0 - wTip) * (float)Math.Pow(1 - t, 1.15);
            double r = h[i] * Math.PI / 180.0;
            float nx = (float)Math.Cos(r), ny = (float)Math.Sin(r); // right-hand normal
            left[i] = new PointF(c[i].X - nx * w / 2, c[i].Y - ny * w / 2);
            right[i] = new PointF(c[i].X + nx * w / 2, c[i].Y + ny * w / 2);
        }
        var poly = new List<PointF>(left);
        for (int i = N; i >= 0; i--) poly.Add(right[i]);
        s.Polys.Add(poly.ToArray());
        if (dotR > 0)
            s.Dots.Add(new Dot { X = c[N].X, Y = c[N].Y, R = dotR });
    }

    // Build the flower in free coordinates, then fit into [pad, 1-pad].
    public static Shape Build(bool simple, float pad)
    {
        var s = new Shape();
        float ox = 0f, oy = 0f;
        if (simple)
        {
            float[] ang = { -64, -32, 0, 32, 64 };
            float[] len = { 0.58f, 0.70f, 0.76f, 0.70f, 0.58f };
            float[] sw  = { 3, -2, 0, 2, -3 };
            for (int i = 0; i < 5; i++)
            {
                float a = ang[i];
                Stroke(s, ox, oy, len[i], a * 1.0f + sw[i], -a * 0.12f + sw[i] * 0.4f, 1.6f,
                       0.13f, 0, 0.02f, 0.062f, 0);
            }
            s.Dots.Add(new Dot { X = ox, Y = oy, R = 0.075f });
        }
        else
        {
            // recurved petals, low on the flanks (drawn first, behind stamens)
            float[] pang = { -128, -104, 104, 128 };
            float[] plen = { 0.30f, 0.42f, 0.42f, 0.30f };
            for (int i = 0; i < 4; i++)
            {
                float a = pang[i]; float sgn = a < 0 ? -1 : 1;
                Stroke(s, ox, oy, plen[i], a, sgn * 40f, 1.0f, 0.010f, 0.030f, 0.004f, 0, 0.35f);
            }
            float[] ang = { -78, -52, -26, 0, 26, 52, 78 };
            float[] len = { 0.60f, 0.70f, 0.78f, 0.82f, 0.78f, 0.70f, 0.60f };
            float[] sw  = { 2, -3, 3, -1.5f, 2.5f, -2, 3 };
            for (int i = 0; i < 7; i++)
            {
                float a = ang[i];
                Stroke(s, ox, oy, len[i], a * 1.0f + sw[i], -a * 0.14f + sw[i] * 0.4f, 1.7f,
                       0.056f, 0, 0.004f, 0.030f, 0);
            }
            s.Dots.Add(new Dot { X = ox, Y = oy, R = 0.045f });
        }
        float minX = 1e9f, minY = 1e9f, maxX = -1e9f, maxY = -1e9f;
        foreach (var p in s.Polys) foreach (var q in p)
        { minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y); }
        foreach (var d in s.Dots)
        { minX = Math.Min(minX, d.X - d.R); maxX = Math.Max(maxX, d.X + d.R); minY = Math.Min(minY, d.Y - d.R); maxY = Math.Max(maxY, d.Y + d.R); }
        float k = (1 - 2 * pad) / Math.Max(maxX - minX, maxY - minY);
        float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        Func<PointF, PointF> f = q => new PointF(0.5f + (q.X - cx) * k, 0.5f + (q.Y - cy) * k);
        var r = new Shape();
        foreach (var p in s.Polys) { var a = new PointF[p.Length]; for (int i = 0; i < p.Length; i++) a[i] = f(p[i]); r.Polys.Add(a); }
        foreach (var d in s.Dots) { var q = f(new PointF(d.X, d.Y)); r.Dots.Add(new Dot { X = q.X, Y = q.Y, R = d.R * k }); }
        return r;
    }

    static void Draw(Graphics g, Shape s, float px, Color col)
    {
        using (var b = new SolidBrush(col))
        {
            foreach (var p in s.Polys)
            {
                var a = new PointF[p.Length];
                for (int i = 0; i < p.Length; i++) a[i] = new PointF(p[i].X * px, p[i].Y * px);
                g.FillPolygon(b, a, FillMode.Winding);
            }
            foreach (var d in s.Dots)
                g.FillEllipse(b, (d.X - d.R) * px, (d.Y - d.R) * px, 2 * d.R * px, 2 * d.R * px);
        }
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // tile=null -> transparent mark only
    public static Bitmap Render(int size, Color? tile, Color flower)
    {
        bool simple = size <= 40;
        int ss = size >= 128 ? 2 : 4;
        int big = size * ss;
        float pad = tile.HasValue ? (simple ? 0.10f : 0.11f) : 0.02f;
        var shape = Build(simple, pad);
        using (var bmp = new Bitmap(big, big, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                if (tile.HasValue)
                    using (var path = Round(0, 0, big, big, big * 0.22f))
                    using (var b = new SolidBrush(tile.Value)) g.FillPath(b, path);
                Draw(g, shape, big, flower);
            }
            var outb = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(outb))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(bmp, new Rectangle(0, 0, size, size), 0, 0, big, big, GraphicsUnit.Pixel);
            }
            return outb;
        }
    }

    public static string Svg(int vb, string fill)
    {
        var s = Build(false, 0.02f);
        var sb = new StringBuilder();
        var ic = CultureInfo.InvariantCulture;
        sb.AppendFormat(ic, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {0} {0}\" width=\"{0}\" height=\"{0}\">\n", vb);
        sb.AppendFormat(ic, "<title>Lycoris</title>\n<g fill=\"{0}\">\n", fill);
        foreach (var p in s.Polys)
        {
            sb.Append("<path d=\"M");
            for (int i = 0; i < p.Length; i++)
                sb.AppendFormat(ic, "{0}{1:0.##} {2:0.##}", i == 0 ? "" : " L", p[i].X * vb, p[i].Y * vb);
            sb.Append("Z\"/>\n");
        }
        foreach (var d in s.Dots)
            sb.AppendFormat(ic, "<circle cx=\"{0:0.##}\" cy=\"{1:0.##}\" r=\"{2:0.##}\"/>\n", d.X * vb, d.Y * vb, d.R * vb);
        sb.Append("</g>\n</svg>\n");
        return sb.ToString();
    }
}
'@

$tileColor = [System.Drawing.Color]::FromArgb(255, 9, 9, 9)
$white = [System.Drawing.Color]::FromArgb(255, 245, 245, 245)
$red = [System.Drawing.Color]::FromArgb(255, 193, 18, 31)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Save-Png($bmp, $path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }

function Write-Ico($tile, $flower, [string]$path) {
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = [Lycoris]::Render($s, $tile, $flower)
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , @($s, $ms.ToArray()); $bmp.Dispose()
    }
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $dim = if ($p[0] -ge 256) { 0 } else { $p[0] }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$p[1].Length); $bw.Write([UInt32]$offset)
        $offset += $p[1].Length
    }
    foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
}

if ($Variant -in 'A', 'All') {
    Write-Ico $tileColor $white (Join-Path $OutDir 'lycoris-a.ico')
    $b = [Lycoris]::Render(256, $tileColor, $white); Save-Png $b (Join-Path $OutDir 'lycoris-a-256.png'); $b.Dispose()
}
if ($Variant -in 'B', 'All') {
    Write-Ico $tileColor $red (Join-Path $OutDir 'lycoris-b.ico')
    $b = [Lycoris]::Render(256, $tileColor, $red); Save-Png $b (Join-Path $OutDir 'lycoris-b-256.png'); $b.Dispose()
}

if ($Variant -eq 'All') {
    # transparent white mark for the in-app logo
    [System.IO.File]::WriteAllText((Join-Path $OutDir 'lycoris-mark-white.svg'), [Lycoris]::Svg(256, '#F5F5F5'), (New-Object System.Text.UTF8Encoding $false))
    $b = [Lycoris]::Render(256, $null, $white); Save-Png $b (Join-Path $OutDir 'lycoris-mark-white.png'); $b.Dispose()

    # preview sheet: both colorways x dark/light backgrounds x 256/64/32/16 (+ 16/32 magnified)
    $W = 1500; $H = 1155
    $sheet = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'ClearTypeGridFit'
    $g.Clear([System.Drawing.Color]::FromArgb(255, 30, 30, 30))
    $fT = New-Object System.Drawing.Font 'Segoe UI Semibold', 16
    $fS = New-Object System.Drawing.Font 'Segoe UI', 10
    $wb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 235, 235, 235))
    $bb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 20, 20, 20))
    $g.DrawString('Lycoris - icon preview', $fT, $wb, 24, 12)
    $rows = @(@('A  Монохром', $white, 70), @('B  Красный акцент', $red, 610))
    $bgs = @(@([System.Drawing.Color]::FromArgb(255, 22, 22, 22), $wb), @([System.Drawing.Color]::FromArgb(255, 236, 236, 236), $bb))
    foreach ($row in $rows) {
        for ($k = 0; $k -lt 2; $k++) {
            $x0 = 24 + $k * 735; $y0 = $row[2]
            $bgb = New-Object System.Drawing.SolidBrush $bgs[$k][0]
            $g.FillRectangle($bgb, $x0, $y0, 717, 520)
            $g.DrawString($row[0] + $(if ($k -eq 0) { '  /  dark' } else { '  /  light' }), $fT, $bgs[$k][1], $x0 + 14, $y0 + 8)
            $x = $x0 + 20; $yb = $y0 + 60
            foreach ($s in 256, 64, 32, 16) {
                $bmp = [Lycoris]::Render($s, $tileColor, $row[1])
                $g.DrawImage($bmp, $x, $yb + 256 - $s, $s, $s)
                $g.DrawString("$s px", $fS, $bgs[$k][1], $x, $yb + 262)
                $bmp.Dispose(); $x += $s + 34
            }
            # magnified small sizes (nearest neighbour) to inspect pixels
            $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
            $m16 = [Lycoris]::Render(16, $tileColor, $row[1]); $m32 = [Lycoris]::Render(32, $tileColor, $row[1])
            $g.DrawImage($m16, $x0 + 20, $yb + 300, 128, 128)
            $g.DrawImage($m32, $x0 + 180, $yb + 300, 128, 128)
            $g.DrawString('16 x8', $fS, $bgs[$k][1], $x0 + 20, $yb + 426)
            $g.DrawString('32 x4', $fS, $bgs[$k][1], $x0 + 180, $yb + 426)
            # transparent mark on the same background
            $mk = [Lycoris]::Render(128, $null, $(if ($k -eq 0) { $row[1] } else { $tileColor }))
            $g.InterpolationMode = 'HighQualityBicubic'
            $g.DrawImage($mk, $x0 + 340, $yb + 300, 110, 110)
            $g.DrawString('mark', $fS, $bgs[$k][1], $x0 + 340, $yb + 426)
            $m16.Dispose(); $m32.Dispose(); $mk.Dispose()
        }
    }
    $g.Dispose()
    Save-Png $sheet (Join-Path $OutDir 'icon-preview.png'); $sheet.Dispose()
}
"icons written to $OutDir"
