# Draws the wslim icon (same geometry as assets/wslim.svg): a Tux face (own drawing) peeking over a dark terminal
# bar with a mint ">_" prompt, on teal. Small sizes drop the eye highlights and use a bolder prompt.
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
    $dc.DrawEllipse((Brush "#0B0F19"), $null, [Windows.Point]::new(32, 37), 18, 20)
    $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(32, 42.5), 11.5, 7.5)
    foreach ($x in 25.6, 38.4) { $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new($x, 28.5), 4.6, 5.8) }
    $dc.DrawEllipse((Brush "#0B0F19"), $null, [Windows.Point]::new(26.6, 29.6), 2.3, 3.2)
    $dc.DrawEllipse((Brush "#0B0F19"), $null, [Windows.Point]::new(37.4, 29.6), 2.3, 3.2)
    if (-not $small) {
        $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(27.3, 28.4), 0.8, 0.9)
        $dc.DrawEllipse((Brush "#FFFFFF"), $null, [Windows.Point]::new(38.1, 28.4), 0.8, 0.9)
    }
    $dc.DrawEllipse((Brush "#FBBF24"), $null, [Windows.Point]::new(32, 39.2), 5.2, 2.6)
    $dc.DrawEllipse((Brush "#F59E0B"), $null, [Windows.Point]::new(32, 37), 6.6, 3.1)
    $dc.DrawRectangle((Brush "#0F172A"), $null, [Windows.Rect]::new(0, 44, 64, 20))
    $dc.DrawRectangle((Brush "#1E293B"), $null, [Windows.Rect]::new(0, 44, 64, 1.5))
    $pen = New-Object System.Windows.Media.Pen (Brush "#34D399"), ($(if ($small) { 4.2 } else { 3.4 }))
    $pen.StartLineCap = "Round"; $pen.EndLineCap = "Round"; $pen.LineJoin = "Round"
    $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse("M12,49.5 L17,53.5 L12,57.5"))
    $dc.DrawRoundedRectangle((Brush "#34D399"), $null, [Windows.Rect]::new(21, 55.5, 9, 3.4), 1.7, 1.7)
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
