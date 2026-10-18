$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path -Parent $PSScriptRoot
$source=[System.Drawing.Bitmap]::new((Join-Path $root 'src/AiPulse/Assets/pulse.png'))
try {
    foreach($point in @(@(0,0),@(($source.Width-1),0),@(0,($source.Height-1)),@(($source.Width-1),($source.Height-1)))) {
        if($source.GetPixel($point[0],$point[1]).A -gt 1){throw 'Icon corners must be transparent'}
    }
    $frames=@()
    foreach($size in @(16,24,32,48,64,128,256)) {
        $bitmap=[System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($source,0,0,$size,$size)
        $stream=[System.IO.MemoryStream]::new()
        $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
        $frames+=,@{Size=$size;Bytes=$stream.ToArray()}
        $graphics.Dispose();$bitmap.Dispose();$stream.Dispose()
    }
    $output=[System.IO.File]::Create((Join-Path $root 'src/AiPulse/Assets/pulse.ico'))
    $writer=[System.IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
        $offset=6+16*$frames.Count
        foreach($frame in $frames) {
            $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset)
            $offset+=$frame.Bytes.Length
        }
        foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
    } finally {$writer.Dispose();$output.Dispose()}
    Write-Output 'Generated seven icon sizes with transparent corners.'
} finally {$source.Dispose()}
