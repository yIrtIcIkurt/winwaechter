# Builds the Inno Setup wizard images (all DPI sizes) from the assets:
#   assets\installer\wizard-gemini.jpg  -> wizard-<w>x<h>.bmp  (side image, cropped to 164:314)
#   assets\icon\winwaechter-1024.png    -> small-<n>.bmp        (header logo on white)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$inst = Join-Path $root 'assets\installer'
$icon = Join-Path $root 'assets\icon\winwaechter-1024.png'

function Save-Bmp([System.Drawing.Image]$src, [System.Drawing.Rectangle]$from, [int]$w, [int]$h, [string]$path, [System.Drawing.Color]$bg) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear($bg); $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), $from, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
}

# Side image: crop the source to 164:314 keeping the lower part (gauge arcs).
$wiz = [System.Drawing.Image]::FromFile((Join-Path $inst 'wizard-gemini.jpg'))
try {
    $cropH = [int]($wiz.Width * 314 / 164)
    $from = New-Object System.Drawing.Rectangle 0, ($wiz.Height - $cropH), $wiz.Width, $cropH
    foreach ($s in @(@(164,314), @(205,393), @(246,471), @(328,628), @(410,785))) {
        Save-Bmp $wiz $from $s[0] $s[1] (Join-Path $inst "wizard-$($s[0])x$($s[1]).bmp") ([System.Drawing.Color]::White)
    }
} finally { $wiz.Dispose() }

# Header logo: icon centred on white with a small margin.
$ico = [System.Drawing.Image]::FromFile($icon)
try {
    foreach ($n in 55, 69, 83, 110, 138) {
        $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White)
        $g.InterpolationMode = 'HighQualityBicubic'; $m = [int]($n * 0.06)
        $g.DrawImage($ico, $m, $m, $n - 2 * $m, $n - 2 * $m); $g.Dispose()
        $bmp.Save((Join-Path $inst "small-$n.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
    }
} finally { $ico.Dispose() }
"Installer-Bilder geschrieben nach $inst"
