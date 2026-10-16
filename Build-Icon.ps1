# Windows-only deterministic format conversion of the generated transparent artwork.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory=Join-Path $PSScriptRoot 'WinUI/Assets'
$source=[System.Drawing.Bitmap]::new((Join-Path $assetDirectory 'AppIcon.png'))
$sizes=@(16,20,24,32,40,48,64,96,128,256)
$frames=[System.Collections.Generic.List[byte[]]]::new()
try {
 foreach($size in $sizes){
  $bitmap=[System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
  $stream=[System.IO.MemoryStream]::new()
  try {
   $graphics.CompositingMode=[System.Drawing.Drawing2D.CompositingMode]::SourceCopy
   $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $graphics.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
   $graphics.DrawImage($source,[System.Drawing.Rectangle]::new(0,0,$size,$size))
   $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
   $frames.Add($stream.ToArray())
  } finally {$stream.Dispose();$graphics.Dispose();$bitmap.Dispose()}
 }
 $file=[System.IO.File]::Create((Join-Path $assetDirectory 'AppIcon.ico'))
 $writer=[System.IO.BinaryWriter]::new($file)
 try {
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($index=0;$index -lt $sizes.Count;$index++){
   $dimension=if($sizes[$index] -eq 256){0}else{$sizes[$index]}
   $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
   $writer.Write([byte]0);$writer.Write([byte]0)
   $writer.Write([uint16]1);$writer.Write([uint16]32)
   $writer.Write([uint32]$frames[$index].Length);$writer.Write([uint32]$offset)
   $offset+=$frames[$index].Length
  }
  foreach($frame in $frames){$writer.Write($frame)}
 } finally {$writer.Dispose()}
} finally {$source.Dispose()}
Write-Output ('Created transparent icon sizes: '+($sizes -join ', '))
