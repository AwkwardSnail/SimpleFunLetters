# Generates the Awkward sn@ail app icon ("sn@" plus a block cursor, green on
# black) at every size the MSIX package and the .exe need, by resizing
# packaging\icon-master.png (2048 x 2048). Re-run after replacing the master.
#   packaging\Assets\*.png      MSIX logos, tiles and splash screen
#   desktop\SimpleFunLetters\app.ico icon embedded in the .exe and shown on the window

Add-Type -AssemblyName System.Drawing

$Background = [System.Drawing.ColorTranslator]::FromHtml('#0D0F0C')

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$master = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'icon-master.png'))

# Draws the master icon centred on a background-coloured canvas. $scale is the
# icon's size as a fraction of the canvas's shorter side.
function New-IconBitmap([int]$width, [int]$height, [double]$scale) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear($Background)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $side = [int][Math]::Round([Math]::Min($width, $height) * $scale)
    $g.DrawImage($master, [int](($width - $side) / 2), [int](($height - $side) / 2), $side, $side)
    $g.Dispose()
    return $bmp
}

function Save-Png([string]$name, [int]$width, [int]$height, [double]$scale) {
    $bmp = New-IconBitmap $width $height $scale
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$iconScale = 1.0   # app list, taskbar, store logo, .ico (the master already has its margin)
$tileScale = 0.6   # Start menu tiles and splash leave more breathing room

foreach ($s in 100, 200, 400) {
    $f = $s / 100
    Save-Png "Square44x44Logo.scale-$s.png"   (44 * $f)  (44 * $f)  $iconScale
    Save-Png "Square150x150Logo.scale-$s.png" (150 * $f) (150 * $f) $tileScale
    Save-Png "Wide310x150Logo.scale-$s.png"   (310 * $f) (150 * $f) $tileScale
    Save-Png "StoreLogo.scale-$s.png"         (50 * $f)  (50 * $f)  $iconScale
}
foreach ($s in 100, 200) {
    Save-Png "SplashScreen.scale-$s.png" (620 * $s / 100) (300 * $s / 100) $tileScale
}
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Png "Square44x44Logo.targetsize-$t.png" $t $t $iconScale
    Save-Png "Square44x44Logo.altform-unplated_targetsize-$t.png" $t $t $iconScale
}

# .ico with PNG-compressed entries (supported since Windows Vista).
# The PNGs must be 32-bit RGBA: Windows' icon decoder rejects RGB PNGs inside
# an .ico, and WPF then crashes loading the window icon. System.Drawing drops
# the alpha channel when every pixel is opaque, so re-encode through WPF as BGRA.
Add-Type -AssemblyName PresentationCore
$sizes = 16, 24, 32, 48, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size $size $iconScale
    $gdiPng = New-Object System.IO.MemoryStream
    $bmp.Save($gdiPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $gdiPng.Position = 0
    $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create($gdiPng, 'None', 'OnLoad')
    $bgra = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($frame, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bgra))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 means 256
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Join-Path $root 'desktop\SimpleFunLetters\app.ico'), $out.ToArray())
$w.Dispose()
$master.Dispose()

"Icons written to $assets and desktop\SimpleFunLetters\app.ico"
