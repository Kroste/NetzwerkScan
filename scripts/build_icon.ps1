# App-Icon-Generator (PowerShell-Port von build_icon.py).
#
# Warum zwei Varianten: der Arbeitslaptop hat kein Python/Pillow (unter
# WindowsApps liegt dort nur der Store-Stub), Bazzite hat beides. Gleiche
# Geometrie, gleiche Farben, gleiches Ergebnis — bei Design-Änderungen BEIDE
# Skripte anpassen, sonst driften PNG/ICO je nach Rechner auseinander.
#
# Motiv: Radarschirm — konzentrische Ringe, Sweep-Strahl, Mittelpunkt.
#
# Erzeugt:
#   NetScanner/Assets/netscanner.png   (256x256, master für Fenster/Tray/AppImage)
#   NetScanner/Assets/netscanner.ico   (multi-res 16..256 für <ApplicationIcon>)
#
# Aufruf aus dem Repo-Root:  pwsh -File scripts/build_icon.ps1
#
# Diese Datei ist UTF-8 MIT BOM gespeichert. Ohne BOM liest Windows
# PowerShell 5.1 sie als ANSI und macht aus "ä" ein "³".

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$APP_NAME = 'netscanner'

# NetScanner-Palette (siehe NetScanner/App.axaml)
$BG     = [System.Drawing.Color]::FromArgb(255, 14, 20, 27)      # #0E141B
$ACCENT = @(63, 182, 168)                                        # #3FB6A8

function New-Accent([int]$alpha) {
    return [System.Drawing.Color]::FromArgb($alpha, $ACCENT[0], $ACCENT[1], $ACCENT[2])
}

function New-Graphics([System.Drawing.Bitmap]$bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    return $g
}

function Add-RoundedRect($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r, $brush) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    $path.Dispose()
}

function New-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bmp

    # Abgerundetes Quadrat als Grundfläche. Radius gegen die Kantenlänge
    # deckeln, sonst degenerieren bei kleinen Größen die Ecken.
    $corner = [Math]::Max(2, [int]($size * 0.18))
    $bBg = New-Object System.Drawing.SolidBrush $BG
    Add-RoundedRect $g 0 0 ($size - 1) ($size - 1) $corner $bBg
    $bBg.Dispose()

    $cx = $size / 2.0
    $cy = $size / 2.0

    # Unter 32 px verschmelzen drei dünne Ringe zu Matsch: dort nur zwei Ringe,
    # dafür kräftiger und voll deckend. Bei jeder Änderung am Motiv die
    # 16x16-Silhouette gegenprüfen, sie ist der harte Maßstab.
    $small = $size -lt 32
    if ($small) {
        $rings = @(@(0.36, 190), @(0.17, 255))
        $ringW = $size * 0.045
        $sweepW = $size * 0.075
        $reach = $size * 0.33
        $dotR = $size * 0.075
    } else {
        $rings = @(@(0.40, 110), @(0.28, 155), @(0.15, 205))
        $ringW = $size * 0.012
        $sweepW = $size * 0.016
        $reach = $size * 0.38
        $dotR = $size * 0.045
    }

    foreach ($ring in $rings) {
        $r = $size * $ring[0]
        $pen = New-Object System.Drawing.Pen (New-Accent ([int]$ring[1])), ([Math]::Max(1, [int]$ringW))
        $g.DrawEllipse($pen, ($cx - $r), ($cy - $r), (2 * $r), (2 * $r))
        $pen.Dispose()
    }

    # Sweep-Strahl vom Zentrum nach rechts oben (45 Grad).
    $penSweep = New-Object System.Drawing.Pen (New-Accent 255), ([Math]::Max(1, [int]$sweepW))
    $g.DrawLine($penSweep, $cx, $cy, ($cx + $reach), ($cy - $reach))
    $penSweep.Dispose()

    # Mittelpunkt = erfasstes Gerät.
    $bDot = New-Object System.Drawing.SolidBrush (New-Accent 255)
    $g.FillEllipse($bDot, ($cx - $dotR), ($cy - $dotR), (2 * $dotR), (2 * $dotR))
    $bDot.Dispose()

    $g.Dispose()
    return $bmp
}

function Save-Ico([System.Drawing.Bitmap[]]$images, [string]$path) {
    # System.Drawing kann keine Multi-Res-ICOs schreiben — deshalb das
    # ICO-Format von Hand. Eingebettete PNGs sind ab Windows Vista erlaubt
    # und sparen die BMP/AND-Mask-Fummelei.
    $blobs = foreach ($img in $images) {
        $ms = New-Object System.IO.MemoryStream
        $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    try {
        $bw.Write([uint16]0)                  # reserved
        $bw.Write([uint16]1)                  # type: 1 = Icon
        $bw.Write([uint16]$images.Count)

        # Directory-Einträge sind je 16 Byte; Bilddaten folgen dahinter.
        $offset = 6 + 16 * $images.Count
        for ($i = 0; $i -lt $images.Count; $i++) {
            $w = $images[$i].Width
            # 256 wird im ICO-Header als 0 kodiert.
            $bw.Write([byte]($(if ($w -ge 256) { 0 } else { $w })))
            $bw.Write([byte]($(if ($w -ge 256) { 0 } else { $w })))
            $bw.Write([byte]0)                # Farbanzahl (0 = truecolor)
            $bw.Write([byte]0)                # reserved
            $bw.Write([uint16]1)              # color planes
            $bw.Write([uint16]32)             # bits per pixel
            $bw.Write([uint32]$blobs[$i].Length)
            $bw.Write([uint32]$offset)
            $offset += $blobs[$i].Length
        }
        foreach ($blob in $blobs) { $bw.Write($blob) }
    }
    finally {
        $bw.Dispose(); $fs.Dispose()
    }
}

# Repo-relativ: <repo>/scripts/build_icon.ps1 -> <repo>/NetScanner/Assets
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'NetScanner/Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$pngPath = Join-Path $assets "$APP_NAME.png"
$icoPath = Join-Path $assets "$APP_NAME.ico"

$master = New-Icon 256
$master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Geschrieben: $pngPath (256x256)"

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$icons = foreach ($s in $sizes) { New-Icon $s }
Save-Ico $icons $icoPath
Write-Host "Geschrieben: $icoPath (multi-res: $($sizes -join ', '))"

foreach ($i in $icons) { $i.Dispose() }
$master.Dispose()
