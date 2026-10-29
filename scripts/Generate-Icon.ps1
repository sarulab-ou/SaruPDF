#requires -Version 7.0
. "$PSScriptRoot/Common.ps1"
Add-Type -AssemblyName System.Drawing
$directory = Join-Path $ProjectRoot 'assets'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>PDF</title>
  <text x="128" y="166" text-anchor="middle" font-family="Arial, sans-serif" font-weight="700" font-size="112" fill="#ff0000">PDF</text>
</svg>
'@
[System.IO.File]::WriteAllText((Join-Path $directory 'SaruPDF.svg'), $svg + "`n", [System.Text.UTF8Encoding]::new($false))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $matrix = [System.Drawing.Drawing2D.Matrix]::new()
    $family = [System.Drawing.FontFamily]::new('Arial')
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 0))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $path.AddString('PDF', $family, [int][System.Drawing.FontStyle]::Bold, 256.0,
            [System.Drawing.PointF]::new(0, 0), [System.Drawing.StringFormat]::GenericTypographic)
        $bounds = $path.GetBounds()
        $padding = [Math]::Max(1.0, $size / 24.0)
        $scale = [Math]::Min(($size - 2 * $padding) / $bounds.Width, ($size - 2 * $padding) / $bounds.Height)
        $matrix.Translate(-$bounds.X, -$bounds.Y)
        $path.Transform($matrix)
        $matrix.Reset()
        $matrix.Scale($scale, $scale)
        $path.Transform($matrix)
        $matrix.Reset()
        $matrix.Translate(($size - $bounds.Width * $scale) / 2, ($size - $bounds.Height * $scale) / 2)
        $path.Transform($matrix)
        $graphics.FillPath($brush, $path)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images.Add($stream.ToArray())
    } finally {
        $stream.Dispose(); $brush.Dispose(); $family.Dispose(); $matrix.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}
$output = [System.IO.File]::Create((Join-Path $directory 'SaruPDF.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = $(if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] })
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output 'Generated assets/SaruPDF.ico: red PDF text, transparent background, 16–256 px.'
