# Writes a copy of an .ico that keeps only the small frames (16-48 px), for the console exe.
# A console program's icon is only shown at those sizes; dropping the 64 and 256 px frames saves about 10 KB.
param([Parameter(Mandatory)][string]$In, [Parameter(Mandatory)][string]$Out, [int[]]$Sizes = @(16, 20, 24, 32, 48))
$b = [IO.File]::ReadAllBytes($In)
$n = [BitConverter]::ToUInt16($b, 4)
$keep = @()
for ($i = 0; $i -lt $n; $i++) {
  $e = 6 + 16 * $i
  $w = if ($b[$e] -eq 0) { 256 } else { [int]$b[$e] }
  if ($Sizes -contains $w) { $keep += $e }
}
if ($keep.Count -eq 0) { Copy-Item $In $Out; exit 0 }
$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$keep.Count)
$off = 6 + 16 * $keep.Count
foreach ($e in $keep) {
  $bw.Write($b, $e, 8)
  $bw.Write([uint32][BitConverter]::ToUInt32($b, $e + 8))
  $bw.Write([uint32]$off)
  $off += [BitConverter]::ToUInt32($b, $e + 8)
}
foreach ($e in $keep) {
  $bw.Write($b, [int][BitConverter]::ToUInt32($b, $e + 12), [int][BitConverter]::ToUInt32($b, $e + 8))
}
$bw.Flush()
[IO.File]::WriteAllBytes($Out, $ms.ToArray())
