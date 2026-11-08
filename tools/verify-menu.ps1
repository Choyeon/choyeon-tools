param(
    [string]$Log = 'menu-verify.txt'
)

# 提权会话里部署应用、读取设置页传感器状态，再右键内容区截屏看菜单字体与图标。
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$Repo = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Repo '.artifacts'
$null = New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
if (-not [IO.Path]::IsPathRooted($Log)) { $Log = Join-Path $Artifacts $Log }
$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$t) { $lines.Add($t) }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Line "elevated=$elevated"

Get-Process -Name ChoyeonTools -ErrorAction SilentlyContinue | ForEach-Object {
    Add-Line "killing=$($_.Id)"
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

$env:PATH = "C:\Program Files\dotnet;$env:PATH"
$winapp = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winapp.exe'
$launcher = Start-Process -FilePath $winapp -ArgumentList 'run' `
    -WorkingDirectory (Join-Path $Repo 'ChoyeonTools') -PassThru -WindowStyle Hidden
Add-Line "launcherPid=$($launcher.Id)"

$deadline = (Get-Date).AddSeconds(150)
$win = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 1000
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Choyeon Tools')
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($null -ne $win) { break }
}
if ($null -eq $win) {
    Add-Line 'WINDOW NOT FOUND'
    [System.IO.File]::WriteAllLines($Log, $lines, (New-Object System.Text.UTF8Encoding($false)))
    return
}
Add-Line "window=$($win.Current.NativeWindowHandle)"
Start-Sleep -Seconds 35

function Select-Page([string]$name) {
    $li = [System.Windows.Automation.ControlType]::ListItem
    $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $li)))
    foreach ($item in $items) {
        if ($item.Current.Name -eq $name) {
            try {
                $pat = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $pat.Select()
                return $true
            } catch { return $false }
        }
    }
    return $false
}

# 设置页：传感器状态行（含 LastError）写到独立文件，方便中权限进程读取。
[void](Select-Page '设置')
Start-Sleep -Milliseconds 1800
$sb = New-Object System.Text.StringBuilder
$texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
foreach ($t in $texts) {
    $n = $t.Current.Name
    if ($null -ne $n -and $n.Trim().Length -gt 0) { [void]$sb.AppendLine($n.Trim()) }
}
[System.IO.File]::WriteAllText((Join-Path $Artifacts 'settings-now.txt'), $sb.ToString(), [System.Text.Encoding]::UTF8)
Add-Line 'settings-dump=settings-now.txt'

# 回到总览，右键内容区弹出菜单，整屏截屏（菜单是独立弹出窗口，PrintWindow 抓不到）。
[void](Select-Page '总览')
Start-Sleep -Milliseconds 1200
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MouseSim {
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    public static void Force(IntPtr hwnd) {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint cur = GetCurrentThreadId();
        AttachThreadInput(cur, fgThread, true);
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        AttachThreadInput(cur, fgThread, false);
    }
}
'@
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[MouseSim]::Force($hwnd)
Start-Sleep -Milliseconds 600
Add-Line "foreground=$([MouseSim]::GetForegroundWindow()) target=$hwnd"
$rect = $win.Current.BoundingRectangle
$cx = [int]($rect.Left + $rect.Width * 0.6)
$cy = [int]($rect.Top + $rect.Height * 0.5)
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($cx, $cy)
[MouseSim]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
[MouseSim]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 700
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$g.Dispose()
$bmp.Save((Join-Path $Artifacts 'menu-screen.png'))
$bmp.Dispose()
Add-Line "click=$cx,$cy capture=menu-screen.png"

[System.IO.File]::WriteAllLines($Log, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "log=$Log"