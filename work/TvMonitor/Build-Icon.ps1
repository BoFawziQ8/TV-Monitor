param(
    [string]$IconPath = "$PSScriptRoot/TvMonitor.ico",
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$radius) {
    $p = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = $radius * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $canvas = [Drawing.Bitmap]::new(256, 256, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = 'AntiAlias'
    $tile = RoundedRect 5 5 246 246 53
    $screen = RoundedRect 43 55 170 112 18
    $tileFill = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.Rectangle]::new(5,5,246,246),
        [Drawing.Color]::FromArgb(22, 43, 77), [Drawing.Color]::FromArgb(8, 17, 35), 64)
    $rim = [Drawing.Pen]::new([Drawing.Color]::FromArgb(79, 122, 170), 4)
    $screenPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(228, 244, 255), 13)
    $stand = [Drawing.Pen]::new([Drawing.Color]::FromArgb(228, 244, 255), 12)
    $stand.StartCap = $stand.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $play = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(67, 222, 245))
    $triangle = [Drawing.PointF[]]@(
        [Drawing.PointF]::new(110, 83),
        [Drawing.PointF]::new(157, 111),
        [Drawing.PointF]::new(110, 139))
    $graphics.FillPath($tileFill, $tile)
    $graphics.DrawPath($rim, $tile)
    $graphics.DrawPath($screenPen, $screen)
    $graphics.FillPolygon($play, $triangle)
    $graphics.DrawLine($stand, 128, 169, 128, 196)
    $graphics.DrawLine($stand, 95, 199, 161, 199)
    foreach ($resource in $graphics, $tile, $screen, $tileFill, $rim, $screenPen, $stand, $play) { $resource.Dispose() }

    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $scale = [Drawing.Graphics]::FromImage($bitmap)
    $scale.CompositingMode = 'SourceCopy'
    $scale.InterpolationMode = 'HighQualityBicubic'
    $scale.DrawImage($canvas, 0, 0, $size, $size)
    $scale.Dispose()
    $canvas.Dispose()
    $png = [IO.MemoryStream]::new()
    $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $bytes = $png.ToArray()
    $png.Dispose()
    return ,$bytes
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { Render $size }
$file = [IO.BinaryWriter]::new([IO.File]::Create($IconPath))
try {
    $file.Write([uint16]0)
    $file.Write([uint16]1)
    $file.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $edge = [byte]($sizes[$i] % 256)
        $file.Write([byte[]]@($edge, $edge, 0, 0))
        $file.Write([uint16]1)
        $file.Write([uint16]32)
        $file.Write([uint32]$images[$i].Length)
        $file.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($item in $images) { $file.Write([byte[]]$item) }
} finally { $file.Dispose() }
if ($PreviewPath) { [IO.File]::WriteAllBytes($PreviewPath, [byte[]]$images[-1]) }
