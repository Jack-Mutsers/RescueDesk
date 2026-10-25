# Generates RescueDesk.ico (16-256 px) next to this script: a drive with a file coming back out of it.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    function RR($x, $y, $w, $h, $r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [Math]::Max(1, $r * 2)
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); return $p
    }
    # Blue rounded tile
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(59, 130, 246)), ([System.Drawing.Color]::FromArgb(29, 78, 216))
    $g.FillPath($bg, (RR (8 * $k) (8 * $k) (240 * $k) (240 * $k) (52 * $k)))
    $white = [System.Drawing.Brushes]::White
    # Drive body
    $g.FillPath($white, (RR (40 * $k) (164 * $k) (176 * $k) (52 * $k) (12 * $k)))
    $led = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 197, 94))
    $g.FillEllipse($led, 178 * $k, 182 * $k, 18 * $k, 18 * $k)
    if ($s -ge 32) {
        $slot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(191, 219, 254))
        $g.FillRectangle($slot, 62 * $k, 186 * $k, 90 * $k, 10 * $k)
    }
    # Up arrow coming out of the drive
    $shaft = RR (110 * $k) (88 * $k) (36 * $k) (64 * $k) (4 * $k)
    $g.FillPath($white, $shaft)
    $head = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (128 * $k), (34 * $k)),
        (New-Object System.Drawing.PointF (188 * $k), (100 * $k)),
        (New-Object System.Drawing.PointF (68 * $k), (100 * $k)))
    $g.FillPolygon($white, $head)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'RescueDesk.ico'), $out.ToArray())
