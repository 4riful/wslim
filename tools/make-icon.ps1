# Draws the wslim icon: a flat blue tile with three bars that get shorter (a disk getting slimmer).
# Usage: powershell -File tools\make-icon.ps1 -Out assets\wslim.ico
param([Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName PresentationCore, WindowsBase
function Draw([int]$s) {
    $k = $s / 64.0
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $blue  = New-Object System.Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0x24, 0x63, 0xEB))
    $white = [Windows.Media.Brushes]::White
    $dc.DrawRoundedRectangle($blue, $null, [Windows.Rect]::new(2*$k, 2*$k, 60*$k, 60*$k), 14*$k, 14*$k)
    foreach ($b in @(@(14, 18, 36), @(14, 29, 26), @(14, 40, 16))) {
        $dc.DrawRoundedRectangle($white, $null, [Windows.Rect]::new($b[0]*$k, $b[1]*$k, $b[2]*$k, 6*$k), 3*$k, 3*$k)
    }
    $dc.Close()
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
"wrote $Out"
