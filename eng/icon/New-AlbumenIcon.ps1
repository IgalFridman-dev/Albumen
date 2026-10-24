<#
.SYNOPSIS
Draws the Albumen app icon and packs it as one PNG-compressed .ico.

.DESCRIPTION
A rounded square filled with the blue-to-violet gradient and the white stacked-photo mark of
the logo tile in MainWindow.xaml, at every size Windows asks for. GDI+ only (System.Drawing,
present on every Windows PowerShell host); nothing in the output depends on time or machine
state, so running the script again reproduces the committed file byte for byte.
#>
[CmdletBinding()]
param(
    # Defaults to the committed src\Albumen.App\Assets\Albumen.ico.
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot '..\..\src\Albumen.App\Assets\Albumen.ico' }

$sizes = 16, 20, 24, 32, 48, 64, 128, 256

function New-RoundedSquare([single] $Edge, [single] $Radius) {
    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($Edge - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($Edge - $diameter, $Edge - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $Edge - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $path
}

function New-Points([single[]] $Coordinates) {
    $points = New-Object 'System.Drawing.PointF[]' ($Coordinates.Count / 2)
    for ($index = 0; $index -lt $points.Count; $index++) {
        $points[$index] = New-Object System.Drawing.PointF($Coordinates[2 * $index], $Coordinates[2 * $index + 1])
    }
    , $points
}

# The logo tile's Path data: a photo, the edge of a second one behind it, and a hill line.
# Its geometry spans 3..18 on both axes and fills half the tile.
function New-Mark([int] $Size) {
    $mark = New-Object System.Drawing.Drawing2D.GraphicsPath
    $mark.AddPolygon((New-Points 3, 6, 15, 6, 15, 18, 3, 18))
    $mark.StartFigure()
    $mark.AddLines((New-Points 7, 3, 18, 3, 18, 14))
    $mark.StartFigure()
    $mark.AddLines((New-Points 5, 15, 8, 11.5, 11, 14.5, 13, 12.5))
    $scale = [single]($Size * 0.5 / 15)
    $offset = [single]($Size / 2 - 10.5 * $scale)
    $matrix = New-Object System.Drawing.Drawing2D.Matrix($scale, 0, 0, $scale, $offset, $offset)
    $mark.Transform($matrix)
    $matrix.Dispose()
    $mark
}

function New-IconPng([int] $Size) {
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $tile = New-RoundedSquare $Size ($Size * 0.22)
    $mark = New-Mark $Size
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($Size, $Size)),
        [System.Drawing.Color]::FromArgb(255, 0x3B, 0x82, 0xF6),
        [System.Drawing.Color]::FromArgb(255, 0x8B, 0x5C, 0xF6))
    $gradient.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    # The tile draws the mark 2 px wide at 36 px; small icons keep it legible.
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single][Math]::Max(1.25, $Size * 2.0 / 36))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stream = New-Object System.IO.MemoryStream
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.FillPath($gradient, $tile)
        $graphics.DrawPath($pen, $mark)
        $graphics.Flush()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $pen.Dispose()
        $gradient.Dispose()
        $mark.Dispose()
        $tile.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) { $images.Add((New-IconPng $size)) }

# ICONDIR, one ICONDIRENTRY per size (0 means 256), then the PNG streams in the same order.
$memory = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($memory)
$writer.Write([uint16] 0)
$writer.Write([uint16] 1)
$writer.Write([uint16] $sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $edge = if ($sizes[$index] -ge 256) { 0 } else { $sizes[$index] }
    $writer.Write([byte] $edge)
    $writer.Write([byte] $edge)
    $writer.Write([byte] 0)
    $writer.Write([byte] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] 32)
    $writer.Write([uint32] $images[$index].Length)
    $writer.Write([uint32] $offset)
    $offset += $images[$index].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
[IO.File]::WriteAllBytes($target, $memory.ToArray())
$writer.Dispose()
Write-Output $target
