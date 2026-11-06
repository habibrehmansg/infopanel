# Writes packaging\windows\InfoPanel.ico from Images\logo.png: one PNG-compressed 32-bit entry per size (16, 20, 24,
# 32, 40, 48, 64, 256), each the logo scaled with high-quality bicubic filtering. The .ico is committed; run this only
# when the logo changes, on Windows (it uses System.Drawing):
#   powershell -NoProfile -ExecutionPolicy Bypass -File packaging\windows\make-icon.ps1
param(
  [string]$Source = (Join-Path $PSScriptRoot '..\..\Images\logo.png'),
  [string]$Out = (Join-Path $PSScriptRoot 'InfoPanel.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$logo = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
  $images = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $g = [System.Drawing.Graphics]::FromImage($bitmap)
      try {
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $attributes = New-Object System.Drawing.Imaging.ImageAttributes
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $g.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $logo.Width, $logo.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
      } finally { $g.Dispose() }
      $png = New-Object System.IO.MemoryStream
      $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
      , @($size, $png.ToArray())
    } finally { $bitmap.Dispose() }
  }
} finally { $logo.Dispose() }

# ICONDIR, then one ICONDIRENTRY per image (a width or height of 256 is written as 0), then the PNGs in order.
$stream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
  $size = $image[0]; $bytes = $image[1]
  $dim = if ($size -ge 256) { 0 } else { $size }
  $w.Write([Byte]$dim); $w.Write([Byte]$dim); $w.Write([Byte]0); $w.Write([Byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]$bytes.Length); $w.Write([UInt32]$offset)
  $offset += $bytes.Length
}
foreach ($image in $images) { $w.Write([byte[]]$image[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Out, $stream.ToArray())
Write-Output ("{0}: {1} entries ({2}), {3} bytes" -f $Out, $images.Count, ($sizes -join ', '), $stream.Length)
