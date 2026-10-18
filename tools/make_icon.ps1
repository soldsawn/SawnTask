# Draws the SawnTask icon (dark rounded square, a cyan heartbeat line) at every size Windows asks
# for and packs them into src\SawnTask\Assets\SawnTask.ico (PNG-compressed entries).
#   powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$out = Join-Path $PSScriptRoot '..\src\SawnTask\Assets\SawnTask.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

# Heartbeat line, as fractions of the icon size.
$line = @(@(0.12, 0.55), @(0.30, 0.55), @(0.38, 0.42), @(0.46, 0.66), @(0.55, 0.20), @(0.64, 0.80), @(0.72, 0.55), @(0.88, 0.55))

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background: a dark rounded square with a slight top-to-bottom gradient and a thin edge.
    $pad = [Math]::Max(0.5, $s * 0.03)
    $bg = New-RoundedRect $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), [System.Drawing.Color]::FromArgb(255, 34, 46, 60), [System.Drawing.Color]::FromArgb(255, 14, 19, 26))
    $g.FillPath($grad, $bg)
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 79, 195, 247)), ([Math]::Max(1, $s * 0.018))
    $g.DrawPath($edge, $bg)

    # The line: a wide faint stroke under a bright one gives a little glow without blur.
    $pts = foreach ($f in $line) { New-Object System.Drawing.PointF ([float]($f[0] * $s)), ([float]($f[1] * $s)) }
    $w = [Math]::Max(1.6, $s * 0.075)
    if ($s -ge 32) {
        $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(55, 79, 195, 247)), ($w * 2.2)
        $glow.LineJoin = 'Round'; $glow.StartCap = 'Round'; $glow.EndCap = 'Round'
        $g.DrawLines($glow, [System.Drawing.PointF[]]$pts)
    }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 79, 195, 247)), $w
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$images = foreach ($s in $sizes) { , (Render $s) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Close()
"wrote $out ($((Get-Item $out).Length) bytes)"

# A 256 px preview next to the icon, for a quick look.
[System.IO.File]::WriteAllBytes((Join-Path $env:TEMP 'sawntask-icon-preview.png'), (Render 256))
