param(
    [string]$Ico = 'ChoyeonTools\Assets\AppIcon.ico',
    [string]$OutDir = 'ChoyeonTools\Assets'
)

# 从 AppIcon.ico的256px帧生成清单引用的全部磁贴PNG，替换掉模板占位图。
$Repo = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Ico)) { $Ico = Join-Path $Repo $Ico }
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $Repo $OutDir }
Add-Type -AssemblyName System.Drawing

$icon = [System.Drawing.Icon]::new($Ico, 256, 256)
$master = $icon.ToBitmap()
$icon.Dispose()

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    return $bmp
}

function Get-Graphics($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    return $g
}

# 逐级减半缩小，避免256直接缩到24时细节糊成一片。
function Save-Square([int]$size, [string]$name) {
    $cur = $master
    $w = $cur.Width
    while ([int]($w / 2) -ge $size) {
        $nw = [Math]::Max([int]($w / 2), $size)
        $next = New-Canvas $nw $nw
        $g = Get-Graphics $next
        $g.DrawImage($cur, (New-Object System.Drawing.Rectangle(0, 0, $nw, $nw)))
        $g.Dispose()
        if (-not [object]::ReferenceEquals($cur, $master)) { $cur.Dispose() }
        $cur = $next
        $w = $nw
    }
    $bmp = New-Canvas $size $size
    $g = Get-Graphics $bmp
    $g.DrawImage($cur, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose()
    if (-not [object]::ReferenceEquals($cur, $master)) { $cur.Dispose() }
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "$name ${size}x${size}"
}

function Save-Centered([int]$w, [int]$h, [int]$iconSize, [string]$name) {
    $bmp = New-Canvas $w $h
    $g = Get-Graphics $bmp
    $x = [int](($w - $iconSize) / 2)
    $y = [int](($h - $iconSize) / 2)
    $g.DrawImage($master, (New-Object System.Drawing.Rectangle($x, $y, $iconSize, $iconSize)))
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "$name ${w}x${h} icon=$iconSize"
}

Save-Square 88 'Square44x44Logo.scale-200.png'
Save-Square 24 'Square44x44Logo.targetsize-24_altform-unplated.png'
Save-Square 48 'Square44x44Logo.targetsize-48_altform-lightunplated.png'
Save-Square 300 'Square150x150Logo.scale-200.png'
Save-Square 50 'StoreLogo.png'
Save-Square 48 'LockScreenLogo.scale-200.png'
Save-Centered 620 300 200 'Wide310x150Logo.scale-200.png'
Save-Centered 620 300 200 'SplashScreen.scale-200.png'
$master.Dispose()