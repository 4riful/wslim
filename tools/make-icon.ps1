# Draws the wslim icon (same geometry as assets/wslim.svg): a winking penguin face (own drawing) peeking over a dark
# terminal bar with a centered mint ">_" prompt, on teal. Small sizes drop the blush and eye shine, and use a bolder prompt.
# Usage: powershell -File tools\make-icon.ps1 -Out assets\wslim.ico
param([Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName PresentationCore, WindowsBase
function Brush($hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
function Draw([int]$s) {
    $k = $s / 64.0; $small = $s -le 24
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $k, $k))
    $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry ([Windows.Rect]::new(2, 2, 60, 60)), 14, 14))
    $dc.DrawRectangle((Brush "#0D9488"), $null, [Windows.Rect]::new(0, 0, 64, 64))
    $dc.DrawEllipse((Brush "#0B0F19"), $null, [Windows.Point]::new(32, 36), 21, 21)
    $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(25.5, 33), 8.6, 9.6)
    $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(38.5, 33), 8.6, 9.6)
    $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(32, 40), 12, 7)
    if (-not $small) { foreach ($x in 21.5, 42.5) { $dc.DrawEllipse((Brush "#FDA4AF"), $null, [Windows.Point]::new($x, 38.6), 3.2, 2) } }
    $dc.DrawEllipse((Brush "#0B0F19"), $null, [Windows.Point]::new(26, 31), 3.3, 4.2)
    if (-not $small) { $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(27.2, 29.5), 1.3, 1.4) }
    $wink = New-Object System.Windows.Media.Pen (Brush "#0B0F19"), 2.4; $wink.StartLineCap = "Round"; $wink.EndLineCap = "Round"
    $dc.DrawGeometry($null, $wink, [Windows.Media.Geometry]::Parse("M35.5,31.5 Q38.5,27.5 41.5,31.5"))
    $dc.DrawEllipse((Brush "#F59E0B"), $null, [Windows.Point]::new(32, 37.6), 4.6, 2.6)
    $dc.DrawEllipse((Brush "#FBBF24"), $null, [Windows.Point]::new(32, 39.4), 3.2, 1.6)
    $dc.DrawRectangle((Brush "#0F172A"), $null, [Windows.Rect]::new(0, 45, 64, 19))
    $dc.DrawRectangle((Brush "#1E293B"), $null, [Windows.Rect]::new(0, 45, 64, 1.5))
    $pen = New-Object System.Windows.Media.Pen (Brush "#34D399"), ($(if ($small) { 4.2 } else { 3.4 }))
    $pen.StartLineCap = "Round"; $pen.EndLineCap = "Round"; $pen.LineJoin = "Round"
    $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse("M23,50.5 L28,54.5 L23,58.5"))
    $dc.DrawRoundedRectangle((Brush "#34D399"), $null, [Windows.Rect]::new(32, 56.5, 9, 3.4), 1.7, 1.7)
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
