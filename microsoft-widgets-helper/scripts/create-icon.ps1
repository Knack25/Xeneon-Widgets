$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
foreach ($variant in @('helper', 'helper-light')) {
$destination = Join-Path $PSScriptRoot "../src/MicrosoftWidgets.Helper/Assets/$variant.ico"
New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
$frames = foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $memory = New-Object IO.MemoryStream
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $scale = $size / 100.0
        $foreground = if ($variant -eq 'helper-light') { '#F4F5F7' } else { '#30383D' }
        foreach ($stroke in @(
            @{Color=$foreground;Points=@(4,20,25,20,44,60,32,79,29,79)},
            @{Color=$foreground;Points=@(51,20,54,20,76,62,65,79,57,79,39,40)},
            @{Color='#9254DE';Points=@(77,20,96,20,79,56,68,36)}
        )) {
            $points = [System.Drawing.PointF[]]@(for ($i=0; $i -lt $stroke.Points.Count; $i+=2) {
                [System.Drawing.PointF]::new([single]($stroke.Points[$i]*$scale), [single]($stroke.Points[$i+1]*$scale))
            })
            $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($stroke.Color))
            try { $graphics.FillPolygon($brush, $points) }
            finally { $brush.Dispose() }
        }
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($size -eq 256) {
            $preview = Join-Path $PSScriptRoot "../dist/$variant-preview.png"
            New-Item -ItemType Directory -Force -Path (Split-Path $preview) | Out-Null
            $bitmap.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        [pscustomobject]@{Size=$size;Bytes=$memory.ToArray()}
    } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$file = [IO.File]::Create([IO.Path]::GetFullPath($destination))
$writer = New-Object IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
}
