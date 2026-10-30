# Generate MUX's Windows icon directly from its canonical vector logo.
# Seven native uncompressed 32-bit DIB frames replace damaged legacy ICO data.
# This ensures Explorer and the taskbar show the high-contrast MUX mark.
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "MUX logo source is missing: $SourcePath"
}

$svg = Get-Content -LiteralPath $SourcePath -Raw
if (-not $svg.Contains('viewBox="0 0 512 512"') -or
    -not $svg.Contains('M120 354V158h48l88 123 88-123h48v196h-52V236l-82 112h-6l-82-112v118h-50z')) {
    throw 'MUX logo geometry has changed; update the native icon vector.'
}
$master = [System.Drawing.Bitmap]::new(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    # Mirrors the canonical docs/assets/mux-logo.svg: dark rounded-square
    # background and a bright M. Render the vector at 512px before downsampling.
    $graphic = [System.Drawing.Graphics]::FromImage($master)
    try {
        $graphic.Clear([System.Drawing.Color]::Transparent)
        $graphic.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $badge = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(10, 10, 11))
        $foreground = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 245, 247))
        try {
            $badge.AddArc(0, 0, 232, 232, 180, 90)
            $badge.AddArc(280, 0, 232, 232, 270, 90)
            $badge.AddArc(280, 280, 232, 232, 0, 90)
            $badge.AddArc(0, 280, 232, 232, 90, 90)
            $badge.CloseFigure()
            $graphic.FillPath($background, $badge)
            [System.Drawing.PointF[]]$points = @(
                [System.Drawing.PointF]::new(120,354), [System.Drawing.PointF]::new(120,158),
                [System.Drawing.PointF]::new(168,158), [System.Drawing.PointF]::new(256,281),
                [System.Drawing.PointF]::new(344,158), [System.Drawing.PointF]::new(392,158),
                [System.Drawing.PointF]::new(392,354), [System.Drawing.PointF]::new(340,354),
                [System.Drawing.PointF]::new(340,236), [System.Drawing.PointF]::new(258,348),
                [System.Drawing.PointF]::new(252,348), [System.Drawing.PointF]::new(170,236),
                [System.Drawing.PointF]::new(170,354))
            $graphic.FillPolygon($foreground, $points)
        } finally {
            $badge.Dispose()
            $background.Dispose()
            $foreground.Dispose()
        }
    } finally { $graphic.Dispose() }
    $frames = @()
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $scaled = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($scaled)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($master, 0, 0, $size, $size)
            } finally { $graphics.Dispose() }

            $buffer = [System.IO.MemoryStream]::new()
            $writer = [System.IO.BinaryWriter]::new($buffer)
            try {
                $writer.Write([int]40)          # BITMAPINFOHEADER size
                $writer.Write([int]$size)       # width
                $writer.Write([int]($size * 2)) # XOR + AND mask height
                $writer.Write([System.Int16]1)         # color planes
                $writer.Write([System.Int16]32)        # BGRA bits per pixel
                $writer.Write([int]0)           # BI_RGB
                $writer.Write([int]($size * $size * 4))
                $writer.Write([int]0)           # x pixels/meter
                $writer.Write([int]0)           # y pixels/meter
                $writer.Write([int]0)           # palette colors
                $writer.Write([int]0)           # important colors

                for ($y = $size - 1; $y -ge 0; $y--) {
                    for ($x = 0; $x -lt $size; $x++) {
                        $p = $scaled.GetPixel($x, $y)
                        $writer.Write([byte]$p.B)
                        $writer.Write([byte]$p.G)
                        $writer.Write([byte]$p.R)
                        $writer.Write([byte]$p.A)
                    }
                }
                $maskRowBytes = [int]([math]::Ceiling($size / 32.0) * 4)
                for ($y = $size - 1; $y -ge 0; $y--) {
                    $maskRow = [byte[]]::new($maskRowBytes)
                    for ($x = 0; $x -lt $size; $x++) {
                        if ($scaled.GetPixel($x, $y).A -lt 128) {
                            $index = [int][math]::Floor($x / 8)
                            $maskRow[$index] = [byte]($maskRow[$index] -bor (128 -shr ($x % 8)))
                        }
                    }
                    $writer.Write($maskRow)
                }
                $writer.Flush()
                $frames += ,@{ Size = $size; Bytes = $buffer.ToArray() }
            } finally {
                $writer.Dispose()
                $buffer.Dispose()
            }
        } finally {
            $scaled.Dispose()
        }
    }

    $parent = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temp = "$OutputPath.tmp"
    $stream = [System.IO.File]::Create($temp)
    $iconWriter = [System.IO.BinaryWriter]::new($stream)
    try {
        $iconWriter.Write([System.UInt16]0)
        $iconWriter.Write([System.UInt16]1)
        $iconWriter.Write([System.UInt16]$frames.Count)
        $offset = 6 + $frames.Count * 16
        foreach ($frame in $frames) {
            $widthByte = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $iconWriter.Write([byte]$widthByte)
            $iconWriter.Write([byte]$widthByte)
            $iconWriter.Write([byte]0)
            $iconWriter.Write([byte]0)
            $iconWriter.Write([System.UInt16]1)
            $iconWriter.Write([System.UInt16]32)
            $iconWriter.Write([System.UInt32]$frame.Bytes.Length)
            $iconWriter.Write([System.UInt32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) {
            $iconWriter.Write([byte[]]$frame.Bytes)
        }
        $iconWriter.Flush()
    } finally {
        $iconWriter.Dispose()
        $stream.Dispose()
    }
    Move-Item -LiteralPath $temp -Destination $OutputPath -Force
    Write-Host "Generated MUX branded icon: $OutputPath ($($frames.Count) native icon sizes)"
} finally {
    $master.Dispose()
}