# Generates the app and tray icons (a circle with a power glyph) into src/SimpleXray.Desktop/Assets.
# The .ico files are committed; rerun only to change the design.
# ICO entries are PNG-compressed (supported since Windows Vista).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\SimpleXray.Desktop\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-Png([int]$size, [string]$color) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($color))
    $pad = [Math]::Max(0.5, $size * 0.02)
    $g.FillEllipse($brush, $pad, $pad, $size - 2 * $pad, $size - 2 * $pad)

    # Power glyph: an open arc and a vertical bar, white with round caps.
    $w = [Math]::Max(1.5, $size * 0.1)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $r = $size * 0.25
    $c = $size / 2
    $cy = $c + $size * 0.03
    $g.DrawArc($pen, $c - $r, $cy - $r, 2 * $r, 2 * $r, -55, 290)
    $g.DrawLine($pen, $c, $size * 0.2, $c, $cy)

    $pen.Dispose(); $brush.Dispose(); $g.Dispose()
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        return , $ms.ToArray()
    }

    # Classic DIB entry: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, empty AND mask.
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    for ($y = 0; $y -lt $size; $y++) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $pixels, ($size - 1 - $y) * $stride, $stride)
    }
    $bmp.UnlockBits($data); $bmp.Dispose()
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($pixels.Length + $maskStride * $size))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    $bw.Write($pixels)
    $bw.Write((New-Object byte[] ($maskStride * $size)))
    $bw.Flush()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes, [string]$color) {
    $images = @($sizes | ForEach-Object { , (New-Png $_ $color) })
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img) }
    $bw.Dispose()
    "written $path"
}

$app = 16, 24, 32, 48, 64, 128, 256
$tray = 16, 20, 24, 32, 40, 48, 64
Write-Ico (Join-Path $out 'app.ico') $app '#2563EB'
Write-Ico (Join-Path $out 'tray-off.ico') $tray '#6B7280'
Write-Ico (Join-Path $out 'tray-busy.ico') $tray '#D97706'
Write-Ico (Join-Path $out 'tray-on.ico') $tray '#16A34A'
Write-Ico (Join-Path $out 'tray-error.ico') $tray '#DC2626'
