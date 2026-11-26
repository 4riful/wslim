# Draws the wslim icon (same geometry as assets/wslim.svg): a tile cut diagonally. Linux half: a penguin.
# Windows half: a terminal prompt with a mint "clean" cursor and a sparkle. Small sizes drop fine details.
# Usage: powershell -File tools\make-icon.ps1 -Out assets\wslim.ico
param([Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName PresentationCore, WindowsBase
function Brush($hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
function Draw([int]$s) {
    $k = $s / 64.0
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $k, $k))
    $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry ([Windows.Rect]::new(2, 2, 60, 60)), 14, 14))
    $small = $s -le 24
    # Linux half: a penguin on pale amber
    $dc.DrawGeometry((Brush "#FDE68A"), $null, [Windows.Media.Geometry]::Parse("M0,0 L61,0 L0,61 Z"))
    $dc.DrawEllipse((Brush "#111827"), $null, [Windows.Point]::new(21, 25), 11, 14)
    $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(21, 29), 7, 9)
    if (-not $small) {
        foreach ($x in 18, 24) { $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new($x, 17), 2.6, 2.6) }
        $dc.DrawEllipse((Brush "#111827"), $null, [Windows.Point]::new(18.6, 17.4), 1.2, 1.2)
        $dc.DrawEllipse((Brush "#111827"), $null, [Windows.Point]::new(23.4, 17.4), 1.2, 1.2)
        $dc.DrawEllipse((Brush "#F59E0B"), $null, [Windows.Point]::new(15.5, 39), 4.2, 2.2)
        $dc.DrawEllipse((Brush "#F59E0B"), $null, [Windows.Point]::new(26, 39), 4.2, 2.2)
    }
    $dc.DrawEllipse((Brush "#F59E0B"), $null, [Windows.Point]::new(21, 21.5), 3.6, 2.2)
    # Windows half: terminal prompt, mint cursor, sparkle
    $dc.DrawGeometry((Brush "#2563EB"), $null, [Windows.Media.Geometry]::Parse("M64,3 L64,64 L3,64 Z"))
    $pen = New-Object System.Windows.Media.Pen (Brush "#FFFFFF"), ($(if ($small) { 5 } else { 4 }))
    $pen.StartLineCap = "Round"; $pen.EndLineCap = "Round"; $pen.LineJoin = "Round"
    $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse("M36,44 L43,50 L36,56"))
    $dc.DrawRoundedRectangle((Brush "#6EE7B7"), $null, [Windows.Rect]::new(46, 52.5, 10, 4), 2, 2)
    if (-not $small) { $dc.DrawGeometry((Brush "#FFFFFF"), $null, [Windows.Media.Geometry]::Parse("M53,30 Q53,36 59,36 Q53,36 53,42 Q53,36 47,36 Q53,36 53,30 Z")) }
    $dc.Pop()
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
