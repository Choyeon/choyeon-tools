param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ProcessName = 'ChoyeonTools'
)

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace WindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
'@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($null -eq $proc) {
    Write-Error 'no main window handle found'
    exit 1
}

$hwnd = $proc.MainWindowHandle
if ([WindowCapture.Native]::IsIconic($hwnd)) {
    [void][WindowCapture.Native]::ShowWindow($hwnd, 9)
    Start-Sleep -Milliseconds 600
}

$rect = New-Object WindowCapture.RECT
[void][WindowCapture.Native]::GetWindowRect($hwnd, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
Write-Output "hwnd=$hwnd size=${width}x${height}"

$bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $graphics.GetHdc()
$ok = [WindowCapture.Native]::PrintWindow($hwnd, $hdc, 2)
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "printwindow=$ok out=$Out"

# 抽样统计颜色，用来判断是否抓成了全黑或全白
$palette = @{}
$step = 6
for ($y = 0; $y -lt $height; $y += $step) {
    for ($x = 0; $x -lt $width; $x += $step) {
        $key = $bmp.GetPixel($x, $y).ToArgb().ToString()
        $palette[$key] = 1 + $palette[$key]
    }
}
$bmp.Dispose()
Write-Output "distinctColors=$($palette.Count)"
$palette.GetEnumerator() | Sort-Object -Descending Value | Select-Object -First 6 | ForEach-Object {
    Write-Output ("argb={0,-12} count={1}" -f [int]($_.Key), $_.Value)
}
