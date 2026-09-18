$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$destination = Join-Path $PSScriptRoot '../src/MicrosoftWidgets.Helper/Assets/helper.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
$frames = foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $memory = New-Object IO.MemoryStream
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $scale = $size / 16.0
        foreach ($tile in @(@(1,2,6,12,'#20B8A6'), @(9,2,6,5,'#F2B84B'), @(9,9,6,5,'#E8F2F4'))) {
            $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tile[4]))
            try { $graphics.FillRectangle($brush, [single]($tile[0]*$scale), [single]($tile[1]*$scale), [single]($tile[2]*$scale), [single]($tile[3]*$scale)) }
            finally { $brush.Dispose() }
        }
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
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
