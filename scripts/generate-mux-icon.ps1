# Regenerate MUX's native Windows icon from the high-resolution README logo.
# Uncompressed 32-bit DIB frames avoid the broken PNG-compressed ICO payloads
# that previously made Explorer and the taskbar use a generic application icon.
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "MUX logo source is missing: $SourcePath"
}

$source = [System.Drawing.Bitmap]::new($SourcePath)
try {
    $frames = @()
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $scaled = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $canvas = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($scaled)
            try {
                $graphics.Clear([System.Drawing.Color]::White)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($source, 0, 0, $size, $size)
            } finally {
                $graphics.Dispose()
            }

            $cornerRadius = [math]::Max(2.0, $size * 0.17)
            for ($y = 0; $y -lt $size; $y++) {
                for ($x = 0; $x -lt $size; $x++) {
                    $pixel = $scaled.GetPixel($x, $y)
                    # Extract the light-grey MUX mark from its white field, then
                    # give it sufficient contrast against the dark icon background.
                    $ink = 255 - [math]::Min($pixel.R, [math]::Min($pixel.G, $pixel.B))
                    $strength = [math]::Min(1.0, [math]::Max(0.0, ($ink - 10.0) / 80.0))
                    $channel = [int][math]::Round(19 + $strength * 222)
                    $xEdge = [math]::Min($x + 0.5, $size - $x - 0.5)
                    $yEdge = [math]::Min($y + 0.5, $size - $y - 0.5)
                    $cx = [math]::Max(0.0, $cornerRadius - $xEdge)
                    $cy = [math]::Max(0.0, $cornerRadius - $yEdge)
                    $distance = [math]::Sqrt($cx * $cx + $cy * $cy)
                    $alpha = [int][math]::Round(255 * [math]::Min(1.0, [math]::Max(0.0, $cornerRadius - $distance + 0.5)))
                    $canvas.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $channel, $channel, $channel))
                }
            }

            $buffer = [System.IO.MemoryStream]::new()
            $writer = [System.IO.BinaryWriter]::new($buffer)
            try {
                $writer.Write([int]40)          # BITMAPINFOHEADER size
                $writer.Write([int]$size)       # width
                $writer.Write([int]($size * 2)) # XOR + AND mask height
                $writer.Write([short]1)         # color planes
                $writer.Write([short]32)        # BGRA bits per pixel
                $writer.Write([int]0)           # BI_RGB
                $writer.Write([int]($size * $size * 4))
                $writer.Write([int]0)           # x pixels/meter
                $writer.Write([int]0)           # y pixels/meter
                $writer.Write([int]0)           # palette colors
                $writer.Write([int]0)           # important colors

                for ($y = $size - 1; $y -ge 0; $y--) {
                    for ($x = 0; $x -lt $size; $x++) {
                        $p = $canvas.GetPixel($x, $y)
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
                        if ($canvas.GetPixel($x, $y).A -lt 128) {
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
            $canvas.Dispose()
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
        $iconWriter.Write([ushort]0)
        $iconWriter.Write([ushort]1)
        $iconWriter.Write([ushort]$frames.Count)
        $offset = 6 + $frames.Count * 16
        foreach ($frame in $frames) {
            $widthByte = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $iconWriter.Write([byte]$widthByte)
            $iconWriter.Write([byte]$widthByte)
            $iconWriter.Write([byte]0)
            $iconWriter.Write([byte]0)
            $iconWriter.Write([ushort]1)
            $iconWriter.Write([ushort]32)
            $iconWriter.Write([uint32]$frame.Bytes.Length)
            $iconWriter.Write([uint32]$offset)
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
    $source.Dispose()
}