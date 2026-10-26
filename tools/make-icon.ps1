# Renders the app icon (a segmented gradient ring, like the menu itself) into a multi-size .ico.
# Run with: powershell -STA -NoProfile -File tools\make-icon.ps1 -Out assets\app.ico
param([string]$Out = (Join-Path $PSScriptRoot '..\assets\app.ico'))

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$ErrorActionPreference = 'Stop'

function Polar([double]$r, [double]$deg, [double]$dx = 0, [double]$dy = 0) {
    $a = $deg * [Math]::PI / 180
    New-Object System.Windows.Point (128 + $dx + $r * [Math]::Sin($a)), (128 + $dy - $r * [Math]::Cos($a))
}

function Wedge([double]$rIn, [double]$rOut, [double]$a0, [double]$a1, [double]$gap, [double]$push) {
    $mid = ($a0 + $a1) / 2 * [Math]::PI / 180
    $dx = [Math]::Sin($mid) * $push; $dy = -[Math]::Cos($mid) * $push
    $gO = $gap / 2 / $rOut * 180 / [Math]::PI; $gI = $gap / 2 / $rIn * 180 / [Math]::PI
    $fig = New-Object System.Windows.Media.PathFigure
    $fig.StartPoint = Polar $rOut ($a0 + $gO) $dx $dy
    $fig.IsClosed = $true
    $fig.Segments.Add((New-Object System.Windows.Media.ArcSegment (Polar $rOut ($a1 - $gO) $dx $dy), (New-Object System.Windows.Size $rOut, $rOut), 0, $false, 'Clockwise', $true))
    $fig.Segments.Add((New-Object System.Windows.Media.LineSegment (Polar $rIn ($a1 - $gI) $dx $dy), $true))
    $fig.Segments.Add((New-Object System.Windows.Media.ArcSegment (Polar $rIn ($a0 + $gI) $dx $dy), (New-Object System.Windows.Size $rIn, $rIn), 0, $false, 'Counterclockwise', $true))
    $g = New-Object System.Windows.Media.PathGeometry
    $g.Figures.Add($fig)
    $g
}

function Render([int]$size) {
    $c = [System.Windows.Media.ColorConverter]
    $stops = New-Object System.Windows.Media.GradientStopCollection
    $stops.Add((New-Object System.Windows.Media.GradientStop ($c::ConvertFromString('#22D3EE')), 0))
    $stops.Add((New-Object System.Windows.Media.GradientStop ($c::ConvertFromString('#8B5CF6')), 0.55))
    $stops.Add((New-Object System.Windows.Media.GradientStop ($c::ConvertFromString('#EC4899')), 1))
    $accent = New-Object System.Windows.Media.LinearGradientBrush $stops, (New-Object System.Windows.Point 0, 0), (New-Object System.Windows.Point 1, 1)
    $accent.MappingMode = 'Absolute'
    $accent.StartPoint = New-Object System.Windows.Point 20, 20
    $accent.EndPoint = New-Object System.Windows.Point 236, 236

    $disk = New-Object System.Windows.Media.RadialGradientBrush
    $disk.GradientStops.Add((New-Object System.Windows.Media.GradientStop ($c::ConvertFromString('#262C44')), 0))
    $disk.GradientStops.Add((New-Object System.Windows.Media.GradientStop ($c::ConvertFromString('#0B0D16')), 1))

    $small = $size -le 24
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256), ($size / 256)))
    $dc.DrawEllipse($disk, (New-Object System.Windows.Media.Pen ((New-Object System.Windows.Media.SolidColorBrush ($c::ConvertFromString('#3A4366')))), 4), (New-Object System.Windows.Point 128, 128), 124, 124)

    $n = if ($small) { 6 } else { 8 }
    $span = 360 / $n
    $gap = if ($small) { 22 } else { 12 }
    for ($i = 0; $i -lt $n; $i++) {
        $a0 = -$span / 2 + $i * $span
        $hot = $i -eq 1
        $geo = Wedge 58 104 $a0 ($a0 + $span) $gap ($(if ($hot) { 12 } else { 0 }))
        $dc.PushOpacity($(if ($hot) { 1.0 } else { 0.78 }))
        $dc.DrawGeometry($accent, $null, $geo)
        $dc.Pop()
    }
    $dc.DrawEllipse($accent, $null, (New-Object System.Windows.Point 128, 128), 30, 30)
    $dc.Pop()
    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $rtb, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
}

function PngBytes($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    $ms.ToArray()
}

# Classic 32-bit DIB entry (bottom-up BGRA + empty AND mask) — read by every icon API, unlike PNG entries.
function DibBytes($bmp, [int]$size) {
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $bmp.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($stride * $size + $maskStride * $size)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $stride) }
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush()
    $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = Render $s
    , $(if ($s -ge 256) { PngBytes $bmp } else { DibBytes $bmp $s })
}

$dir = Split-Path -Parent $Out
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$len); $w.Write([int]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Close()

# Also save a large PNG preview next to the icon.
[System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($Out, '.png'), (PngBytes (Render 256)))
Write-Host "Icon written: $Out"
