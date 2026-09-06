<#
.SYNOPSIS
    Generates the AFKLocker icon assets from a single geometric definition.

.DESCRIPTION
    The icon is defined once, here, as SVG path data on a 48x48 grid. This script
    writes the vector master (assets/afklocker.svg), rasterises it at every size
    Windows needs, and assembles a multi-resolution .ico.

    Rendering uses WPF, which ships with the .NET Framework on every supported
    Windows version, so the assets are reproducible without ImageMagick,
    Inkscape or any other external tool.

.NOTES
    Design brief: a padlock whose body has the proportions of a display, sitting
    on a laptop base. Reads as "locked device that is still on". Deliberately no
    text, no mascot, no night imagery - it should look like a system utility.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

# ---------------------------------------------------------------- geometry ---
# All coordinates are on a 48x48 grid.
$Geometry = [ordered]@{
    # Laptop base: a wide rounded bar, slightly wider than the body above it.
    Base    = 'M 6.4,37.4 L 41.6,37.4 A 1.9,1.9 0 0 1 41.6,41.2 L 6.4,41.2 A 1.9,1.9 0 0 1 6.4,37.4 Z'
    # Padlock body, proportioned like a display (30 x 14.5).
    Body    = 'M 12.2,21 L 35.8,21 A 3.2,3.2 0 0 1 39,24.2 L 39,32.3 A 3.2,3.2 0 0 1 35.8,35.5 L 12.2,35.5 A 3.2,3.2 0 0 1 9,32.3 L 9,24.2 A 3.2,3.2 0 0 1 12.2,21 Z'
    # Shackle: an arc rising from behind the body.
    Shackle = 'M 16.5,22.5 L 16.5,18 A 7.5,7.5 0 0 1 31.5,18 L 31.5,22.5'
}

$Colors = [ordered]@{
    Dark  = '#333340'   # shackle and laptop base
    Blue  = '#0F6CBD'   # lit screen / padlock body
}

$ShackleThickness = 4.0

# ------------------------------------------------------------------- svg -----
function Write-SvgMaster {
    param([string]$Path)

    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" width="48" height="48" role="img" aria-label="AFKLocker">
  <title>AFKLocker</title>
  <!-- Shackle: drawn first so the body overlaps its legs. -->
  <path d="$($Geometry.Shackle)" fill="none" stroke="$($Colors.Dark)" stroke-width="$ShackleThickness" stroke-linecap="round" />
  <!-- Laptop base -->
  <path d="$($Geometry.Base)" fill="$($Colors.Dark)" />
  <!-- Padlock body, proportioned like a lit display -->
  <path d="$($Geometry.Body)" fill="$($Colors.Blue)" />
</svg>
"@
    [System.IO.File]::WriteAllText($Path, $svg, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  svg    -> $Path"
}

# ---------------------------------------------------------------- raster -----
function New-IconBitmap {
    param([int]$Size)

    $visual = New-Object System.Windows.Media.DrawingVisual
    $ctx = $visual.RenderOpen()

    $scale = $Size / 48.0
    $ctx.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))

    $darkBrush = New-Object System.Windows.Media.SolidColorBrush(
        [System.Windows.Media.ColorConverter]::ConvertFromString($Colors.Dark))
    $blueBrush = New-Object System.Windows.Media.SolidColorBrush(
        [System.Windows.Media.ColorConverter]::ConvertFromString($Colors.Blue))

    $shacklePen = New-Object System.Windows.Media.Pen($darkBrush, $ShackleThickness)
    $shacklePen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $shacklePen.EndLineCap = [System.Windows.Media.PenLineCap]::Round

    $ctx.DrawGeometry($null, $shacklePen,
        [System.Windows.Media.Geometry]::Parse($Geometry.Shackle))
    $ctx.DrawGeometry($darkBrush, $null,
        [System.Windows.Media.Geometry]::Parse($Geometry.Base))
    $ctx.DrawGeometry($blueBrush, $null,
        [System.Windows.Media.Geometry]::Parse($Geometry.Body))

    $ctx.Pop()
    $ctx.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Save-Png {
    param($Bitmap, [string]$Path)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = [System.IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

# Returns the raw 32bpp BGRA pixels of a bitmap, bottom-up, as a DIB expects.
function Get-BottomUpBgra {
    param($Bitmap, [int]$Size)

    $stride = $Size * 4
    $pixels = New-Object 'byte[]' ($stride * $Size)
    $converted = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap(
        $Bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $converted.CopyPixels($pixels, $stride, 0)

    $flipped = New-Object 'byte[]' ($stride * $Size)
    for ($row = 0; $row -lt $Size; $row++) {
        [Array]::Copy($pixels, $row * $stride, $flipped, ($Size - 1 - $row) * $stride, $stride)
    }
    # Leading comma: without it PowerShell unrolls the array into the pipeline
    # and the caller receives an Object[] of single bytes.
    return ,$flipped
}

# --------------------------------------------------------------- ico file ----
function Save-Ico {
    param([int[]]$Sizes, [string]$Path)

    # Sizes up to 64 are stored as uncompressed DIBs for maximum compatibility
    # with shell surfaces; larger sizes are stored as PNG, which is what the
    # format expects for 256px entries.
    $entries = @()
    foreach ($size in $Sizes) {
        $bitmap = New-IconBitmap -Size $size
        if ($size -ge 128) {
            $memory = New-Object System.IO.MemoryStream
            $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
            $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $encoder.Save($memory)
            $entries += [pscustomobject]@{ Size = $size; Data = $memory.ToArray() }
            $memory.Dispose()
        }
        else {
            [byte[]]$xor = Get-BottomUpBgra -Bitmap $bitmap -Size $size
            # AND mask: one bit per pixel, rows padded to 4 bytes. All zero -
            # the alpha channel carries transparency.
            [int]$maskStride = [math]::Ceiling($size / 32.0) * 4
            [byte[]]$mask = New-Object 'byte[]' ($maskStride * $size)

            $header = New-Object System.IO.MemoryStream
            $writer = New-Object System.IO.BinaryWriter($header)
            $writer.Write([uint32]40)          # biSize
            $writer.Write([int32]$size)        # biWidth
            $writer.Write([int32]($size * 2))  # biHeight (XOR + AND)
            $writer.Write([uint16]1)           # biPlanes
            $writer.Write([uint16]32)          # biBitCount
            $writer.Write([uint32]0)           # biCompression = BI_RGB
            $writer.Write([uint32]($xor.Length + $mask.Length))
            $writer.Write([int32]0); $writer.Write([int32]0)
            $writer.Write([uint32]0); $writer.Write([uint32]0)
            $writer.Write($xor, 0, $xor.Length)
            $writer.Write($mask, 0, $mask.Length)
            $writer.Flush()
            $entries += [pscustomobject]@{ Size = $size; Data = $header.ToArray() }
            $writer.Dispose()
        }
    }

    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0)                 # reserved
        $writer.Write([uint16]1)                 # type: icon
        $writer.Write([uint16]$entries.Count)

        $offset = 6 + (16 * $entries.Count)
        foreach ($entry in $entries) {
            $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
            $writer.Write([byte]$dimension)      # width
            $writer.Write([byte]$dimension)      # height
            $writer.Write([byte]0)               # palette colours
            $writer.Write([byte]0)               # reserved
            $writer.Write([uint16]1)             # colour planes
            $writer.Write([uint16]32)            # bits per pixel
            $writer.Write([uint32]$entry.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += $entry.Data.Length
        }
        foreach ($entry in $entries) { $writer.Write($entry.Data) }
    }
    finally {
        $writer.Dispose()
    }
    Write-Host "  ico    -> $Path"
}

# ------------------------------------------------------------------ main -----
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Write-Host "Building AFKLocker icon assets"
Write-SvgMaster -Path (Join-Path $OutputDirectory 'afklocker.svg')

foreach ($size in 256, 128) {
    $png = Join-Path $OutputDirectory ("afklocker-{0}.png" -f $size)
    Save-Png -Bitmap (New-IconBitmap -Size $size) -Path $png
    Write-Host "  png    -> $png"
}

Save-Ico -Sizes @(16, 24, 32, 48, 64, 128, 256) -Path (Join-Path $OutputDirectory 'afklocker.ico')

Write-Host "Done."
