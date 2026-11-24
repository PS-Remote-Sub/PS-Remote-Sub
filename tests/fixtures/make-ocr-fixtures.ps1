# Generates OCR test fixtures. Run: powershell -NoProfile -File tests/fixtures/make-ocr-fixtures.ps1
# Test tooling only (Windows PowerShell 5.1 + System.Drawing). Output is deterministic.
#
# Not made here: phone-moses.png, phone-linda.png and dialogue-two-speakers.png are crops of real PS Remote Play
# screenshots of GTA 6 (full window, WebP, 2000 px wide; System.Drawing cannot read WebP, so they were decoded and
# cropped once with SkiaSharp's SKBitmap.Decode + ExtractSubset, saved as PNG). Each crop is what a zone's OCR box
# would cover, as x, y, width, height in the screenshot:
#   phone-moses.png            890, 218, 550, 707   (2000x1125: the group chat below its header)
#   phone-linda.png            890, 205, 520, 665   (2000x1078: Linda's chat below its header)
#   dialogue-two-speakers.png  300, 870, 1400, 140  (2000x1078: the two spoken subtitle lines)
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot 'ocr'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    return @{ Bmp = $bmp; G = $g }
}

# --- en-two-lines.png: white Segoe UI 40px on black ---
$c = New-Canvas 1280 160
$c.G.Clear([System.Drawing.Color]::Black)
$font = New-Object System.Drawing.Font('Segoe UI', 40, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$c.G.DrawString('We have to leave', $font, [System.Drawing.Brushes]::White, 20, 10)
$c.G.DrawString('before sunset.', $font, [System.Drawing.Brushes]::White, 20, 80)
$c.Bmp.Save((Join-Path $outDir 'en-two-lines.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$c.G.Dispose(); $c.Bmp.Dispose()

# --- en-white-on-scene.png: white text, 3px black outline, noisy mid-grey ---
$c = New-Canvas 1280 120
$rng = New-Object System.Random(12345)
for ($y = 0; $y -lt 120; $y++) {
    for ($x = 0; $x -lt 1280; $x++) {
        $v = 128 + $rng.Next(-40, 41)
        $c.Bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $v, $v, $v))
    }
}
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$family = New-Object System.Drawing.FontFamily('Segoe UI')
$fmt = [System.Drawing.StringFormat]::GenericDefault
$path.AddString('Where is the key?', $family, [int][System.Drawing.FontStyle]::Bold, 48, (New-Object System.Drawing.Point(24, 20)), $fmt)
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::Black, 3)
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$c.G.DrawPath($pen, $path)
$c.G.FillPath([System.Drawing.Brushes]::White, $path)
$c.Bmp.Save((Join-Path $outDir 'en-white-on-scene.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$c.G.Dispose(); $c.Bmp.Dispose()
Write-Host "Wrote fixtures to $outDir"
