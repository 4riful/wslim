# Draws the wslim icon (same geometry as assets/wslim.svg): a blue tile with a terminal prompt ">_"
# whose cursor is mint ("clean"), and a small sparkle. The sparkle is left out at 24 px and below.
# Usage: powershell -File tools\make-icon.ps1 -Out assets\wslim.ico
param([Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName PresentationCore, WindowsBase
function Brush($hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
function Draw([int]$s) {
    $k = $s / 64.0
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $k, $k))
    $dc.DrawRoundedRectangle((Brush "#2563EB"), $null, [Windows.Rect]::new(2, 2, 60, 60), 14, 14)
    $pen = New-Object System.Windows.Media.Pen (Brush "#FFFFFF"), 6
    $pen.StartLineCap = "Round"; $pen.EndLineCap = "Round"; $pen.LineJoin = "Round"
    $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse("M17,22 L29,32 L17,42"))
    $dc.DrawRoundedRectangle((Brush "#6EE7B7"), $null, [Windows.Rect]::new(33, 39, 16, 6), 3, 3)
    if ($s -gt 24) { $dc.DrawGeometry((Brush "#FFFFFF"), $null, [Windows.Media.Geometry]::Parse("M47,11 Q47,19 55,19 Q47,19 47,27 Q47,19 39,19 Q47,19 47,11 Z")) }
    $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream; $enc.Save($ms); return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = $sizes | ForEach-Object { ,(Draw $_) }
$fs = [IO.File]::Create($Out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = [byte]($(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write($b); $bw.Write($b); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset); $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
[IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($Out, ".png"), (Draw 256))
# contact sheet for checking small sizes on light and dark backgrounds
$sheet = New-Object System.Windows.Media.DrawingVisual; $dc = $sheet.RenderOpen()
$dc.DrawRectangle((Brush "#F3F3F3"), $null, [Windows.Rect]::new(0, 0, 420, 90)); $dc.DrawRectangle((Brush "#202020"), $null, [Windows.Rect]::new(0, 90, 420, 90))
$x = 10
foreach ($s in 16, 20, 24, 32, 48, 64) {
    foreach ($y in 10, 100) {
        $ms = New-Object IO.MemoryStream (,(Draw $s)); $img = [System.Windows.Media.Imaging.BitmapFrame]::Create($ms)
        $dc.DrawImage($img, [Windows.Rect]::new($x, $y + (64 - $s) / 2, $s, $s))
    }
    $x += $s + 24
}
$dc.Close()
$bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap 420, 180, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32); $bmp.Render($sheet)
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$f = [IO.File]::Create([IO.Path]::Combine([IO.Path]::GetDirectoryName($Out), "icon-sizes.png")); $enc.Save($f); $f.Close()
"wrote $Out"
