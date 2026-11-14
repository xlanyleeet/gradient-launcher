Add-Type -AssemblyName System.Drawing
$srcPath = Join-Path $PSScriptRoot "..\apps\app-frontend\src\assets\gradient-logo.png"
$iconsDir = Join-Path $PSScriptRoot "..\apps\app\icons"

$src = [System.Drawing.Image]::FromFile($srcPath)

function Resize-And-Save($w, $h, $dest) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($src, 0, 0, $w, $h)
    $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
}

Resize-And-Save 128 128 (Join-Path $iconsDir "128x128.png")
Resize-And-Save 256 256 (Join-Path $iconsDir "128x128@2x.png")
Resize-And-Save 512 512 (Join-Path $iconsDir "icon.png")

# Generate .ico from 256x256 bitmap
$bmp256 = New-Object System.Drawing.Bitmap(256, 256)
$g256 = [System.Drawing.Graphics]::FromImage($bmp256)
$g256.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g256.DrawImage($src, 0, 0, 256, 256)
$hIcon = $bmp256.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)
$fs = [System.IO.File]::OpenWrite((Join-Path $iconsDir "icon.ico"))
$icon.Save($fs)
$fs.Close()
$g256.Dispose()
$bmp256.Dispose()
$src.Dispose()

Write-Host "Icons successfully updated from gradient-logo.png!"
