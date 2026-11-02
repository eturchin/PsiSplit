# Generates the PsiSplit application icon and toolbar logo.
# Run from PowerShell: powershell -ExecutionPolicy Bypass -File tools\generate-icon.ps1

param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\PsiSplit.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function New-LogoBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $radius = [float]($Size * 0.24)
        $diameter = [float]($radius * 2)
        $edge = [float]([Math]::Max(0.5, $Size * 0.015))
        $bounds = New-Object System.Drawing.RectangleF($edge, $edge, [float]($Size - 2 * $edge), [float]($Size - 2 * $edge))

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        try {
            $path.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
            $path.AddArc([float]($bounds.Right - $diameter), $bounds.Top, $diameter, $diameter, 270, 90)
            $path.AddArc([float]($bounds.Right - $diameter), [float]($bounds.Bottom - $diameter), $diameter, $diameter, 0, 90)
            $path.AddArc($bounds.Left, [float]($bounds.Bottom - $diameter), $diameter, $diameter, 90, 90)
            $path.CloseFigure()

            $start = New-Object System.Drawing.PointF(0, 0)
            $finish = New-Object System.Drawing.PointF([float]$Size, [float]$Size)
            $top = [System.Drawing.Color]::FromArgb(255, 88, 150, 255)
            $bottom = [System.Drawing.Color]::FromArgb(255, 24, 72, 208)
            $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush($start, $finish, $top, $bottom)
            try {
                $graphics.FillPath($gradient, $path)
            }
            finally {
                $gradient.Dispose()
            }

            $highlightWidth = [float]([Math]::Max(0.8, $Size * 0.022))
            $highlightColor = [System.Drawing.Color]::FromArgb(72, 255, 255, 255)
            $highlight = New-Object System.Drawing.Pen($highlightColor, $highlightWidth)
            try {
                $graphics.DrawPath($highlight, $path)
            }
            finally {
                $highlight.Dispose()
            }
        }
        finally {
            $path.Dispose()
        }

        $fontSize = [float]($Size * 0.68)
        $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        try {
            $format = New-Object System.Drawing.StringFormat
            try {
                $format.Alignment = [System.Drawing.StringAlignment]::Center
                $format.LineAlignment = [System.Drawing.StringAlignment]::Center

                $textBounds = New-Object System.Drawing.RectangleF(0, [float]($Size * -0.035), [float]$Size, [float]$Size)
                $shadowColor = [System.Drawing.Color]::FromArgb(80, 8, 20, 54)
                $shadow = New-Object System.Drawing.SolidBrush($shadowColor)
                try {
                    $shadowBounds = New-Object System.Drawing.RectangleF(0, [float]($Size * 0.01), [float]$Size, [float]$Size)
                    $graphics.DrawString([char]0x03A8, $font, $shadow, $shadowBounds, $format)
                }
                finally {
                    $shadow.Dispose()
                }

                $graphics.DrawString([char]0x03A8, $font, [System.Drawing.Brushes]::White, $textBounds, $format)
            }
            finally {
                $format.Dispose()
            }
        }
        finally {
            $font.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function New-DibFrame {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [int]$Size
    )

    $rectangle = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
    $locked = $Bitmap.LockBits(
        $rectangle,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $stride = $locked.Stride
        $pixels = New-Object byte[] ($stride * $Size)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($locked)
    }

    $maskStride = [int]([Math]::Floor(($Size + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskStride * $Size)

    $stream = New-Object System.IO.MemoryStream
    try {
        $writer = New-Object System.IO.BinaryWriter($stream)
        try {
            # BITMAPINFOHEADER
            $writer.Write([int]40)
            $writer.Write([int]$Size)
            $writer.Write([int]($Size * 2))
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([int]0)
            $writer.Write([int]($Size * $Size * 4))
            $writer.Write([int]0)
            $writer.Write([int]0)
            $writer.Write([int]0)
            $writer.Write([int]0)

            # XOR bitmap, bottom-up BGRA rows.
            for ($y = $Size - 1; $y -ge 0; $y--) {
                $writer.Write($pixels, $y * $stride, $Size * 4)
            }

            # AND mask: fully transparent pixels are already handled by alpha.
            $writer.Write($mask)
        }
        finally {
            $writer.Dispose()
        }

        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = New-Object System.Collections.Generic.List[byte[]]

foreach ($size in $sizes) {
    $bitmap = New-LogoBitmap -Size $size
    try {
        $frames.Add([byte[]](New-DibFrame -Bitmap $bitmap -Size $size))
    }
    finally {
        $bitmap.Dispose()
    }
}

$pngBitmap = New-LogoBitmap -Size 256
try {
    $pngBitmap.Save((Join-Path $OutputDirectory 'PsiSplit.png'), [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $pngBitmap.Dispose()
}

$icoPath = Join-Path $OutputDirectory 'PsiSplit.ico'
$file = [System.IO.File]::Create($icoPath)
try {
    $writer = New-Object System.IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)

        $offset = 6 + 16 * $frames.Count
        for ($i = 0; $i -lt $frames.Count; $i++) {
            $size = $sizes[$i]
            $dimension = if ($size -ge 256) { [byte]0 } else { [byte]$size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([int]$frames[$i].Length)
            $writer.Write([int]$offset)
            $offset += $frames[$i].Length
        }

        foreach ($frame in $frames) {
            $writer.Write($frame)
        }
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $file.Dispose()
}

Write-Host "Icon written to $icoPath"
Write-Host "Logo written to $(Join-Path $OutputDirectory 'PsiSplit.png')"
