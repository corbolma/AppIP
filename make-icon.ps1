# Genera appip.ico (multi-risoluzione, PNG interni) e un'anteprima icon-preview.png
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.03)          # margine
    $w = $s - 2 * $m
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $bg = New-RoundRect $m $m $w $w ($s * 0.22)

    # Sfondo: gradiente diagonale blu -> ciano
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 37, 70, 235)),
        ([System.Drawing.Color]::FromArgb(255, 6, 190, 220)), 45.0
    $g.FillPath($grad, $bg)

    # Riflesso morbido in alto
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)),
        ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.FillPath($hl, $bg)

    # Globo (solo dalle dimensioni medie in su, a 16-24 px sarebbe rumore)
    if ($s -ge 32) {
        $gw = [Math]::Max(1.0, $s * 0.035)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(95, 255, 255, 255)), $gw
        $cx = $s * 0.5; $cy = $s * 0.5; $r = $s * 0.33
        $g.DrawEllipse($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
        $g.DrawEllipse($pen, $cx - $r * 0.45, $cy - $r, $r * 0.9, 2 * $r)
        $g.DrawLine($pen, $cx, $cy - $r, $cx, $cy + $r)
        $g.DrawLine($pen, $cx - $r, $cy, $cx + $r, $cy)
        $yo = $r * 0.55; $xo = [Math]::Sqrt($r * $r - $yo * $yo)
        $g.DrawLine($pen, $cx - $xo, $cy - $yo, $cx + $xo, $cy - $yo)
        $g.DrawLine($pen, $cx - $xo, $cy + $yo, $cx + $xo, $cy + $yo)
    }

    # Testo "IP" con ombra leggera
    $fs = if ($s -le 16) { $s * 0.66 } elseif ($s -le 24) { $s * 0.6 } else { $s * 0.5 }
    $font = New-Object System.Drawing.Font 'Segoe UI', $fs, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $tr = New-Object System.Drawing.RectangleF 0, ($s * 0.02), $s, $s
    if ($s -ge 32) {
        $sh = New-Object System.Drawing.RectangleF ($s * 0.02), ($s * 0.05), $s, $s
        $shb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 10, 30, 90))
        $g.DrawString('IP', $font, $shb, $sh, $sf)
    }
    $g.DrawString('IP', $font, [System.Drawing.Brushes]::White, $tr, $sf)

    # Puntino verde "connesso" in basso a destra
    if ($s -ge 24) {
        $dr = $s * 0.085
        $dx = $s * 0.82; $dy = $s * 0.82
        $ring = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $g.FillEllipse($ring, $dx - $dr * 1.35, $dy - $dr * 1.35, $dr * 2.7, $dr * 2.7)
        $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 34, 197, 94))
        $g.FillEllipse($dot, $dx - $dr, $dy - $dr, $dr * 2, $dr * 2)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $b.Dispose()
}

# Scrive il file .ico
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir 'appip.ico'), $out.ToArray())

# Anteprima: tutte le dimensioni affiancate su sfondo chiaro e scuro
$pw = 20; foreach ($s in $sizes) { $pw += $s + 20 }
$prev = New-Object System.Drawing.Bitmap $pw, 620
$pg = [System.Drawing.Graphics]::FromImage($prev)
$pg.Clear([System.Drawing.Color]::FromArgb(245, 245, 245))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 310, $pw, 310)
$x = 20
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $pg.DrawImage($b, $x, 290 - $s); $pg.DrawImage($b, $x, 600 - $s)
    $x += $s + 20; $b.Dispose()
}
$prev.Save((Join-Path $dir 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "Creati appip.ico e icon-preview.png"
