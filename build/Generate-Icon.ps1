# Generates an original ProPS1 Studio icon (rounded square gradient + conversion chevrons)
# Produces PNG frames and a PNG-in-ICO container. No third-party assets.
param([string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\ProPS1Studio.App\Assets'))

Add-Type -AssemblyName System.Drawing
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

function New-IconPng {
    param([int]$Size)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, $Size * 0.06)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($Size - 2 * $pad), ($Size - 2 * $pad))
    $radius = [Math]::Max(2, $Size * 0.22)

    # background rounded rect with diagonal gradient (indigo -> teal)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 79, 70, 229), [System.Drawing.Color]::FromArgb(255, 8, 145, 178), 45)
    $g.FillPath($brush, $path)

    # subtle top highlight
    $hlRect = New-Object System.Drawing.RectangleF($rect.X, $rect.Y, $rect.Width, ($rect.Height * 0.45))
    $hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($hlRect, [System.Drawing.Color]::FromArgb(70, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90)
    $g.FillPath($hlBrush, $path)

    # two conversion chevrons (PS1 -> EXE metaphor)
    $cx = $Size / 2.0
    $cy = $Size / 2.0
    $h = $Size * 0.30
    $w = $Size * 0.16
    $thick = [Math]::Max(1.0, $Size * 0.09)
    $gap = $Size * 0.13
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    for ($i = 0; $i -lt 2; $i++) {
        $ox = $cx - $gap + ($i * $gap * 1.55) - ($w / 2)
        $alpha = if ($i -eq 0) { 230 } else { 255 }
        $brushW = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255))
        $pts = New-Object System.Drawing.PointF[] 6
        $pts[0] = New-Object System.Drawing.PointF(($ox), ($cy - $h))
        $pts[1] = New-Object System.Drawing.PointF(($ox + $w), ($cy))
        $pts[2] = New-Object System.Drawing.PointF(($ox), ($cy + $h))
        $pts[3] = New-Object System.Drawing.PointF(($ox + $thick), ($cy + $h))
        $pts[4] = New-Object System.Drawing.PointF(($ox + $w + $thick), ($cy))
        $pts[5] = New-Object System.Drawing.PointF(($ox + $thick), ($cy - $h))
        $g.FillPolygon($brushW, $pts)
        $brushW.Dispose()
    }
    $white.Dispose(); $brush.Dispose(); $hlBrush.Dispose(); $path.Dispose(); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bytes = $ms.ToArray(); $ms.Dispose()
    return , $bytes
}

$frames = @()
foreach ($s in @(16, 24, 32, 48, 64, 128, 256)) {
    $png = New-IconPng -Size $s
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir ("icon_$s.png")), $png)
    $frames += [PSCustomObject]@{ Size = $s; Bytes = $png }
}

# assemble ICO (PNG entries)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $side = if ($f.Size -ge 256) { 0 } else { [byte]$f.Size }
    $bw.Write([byte]$side); $bw.Write([byte]$side); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$f.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()
$icoPath = Join-Path $OutDir 'props1.ico'
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Output "ICO written: $icoPath ($((Get-Item $icoPath).Length) bytes)"
