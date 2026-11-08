param(
    [string]$Log = 'elevated-verify.txt'
)

# 提权环境里启动应用并抓取界面文本：中权限进程的 UIA 看不到高权限窗口，两步必须在同一个提权会话里做。
$Repo = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Repo '.artifacts'
$null = New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
if (-not [IO.Path]::IsPathRooted($Log)) { $Log = Join-Path $Artifacts $Log }
$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$text) { $lines.Add($text) }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Line "elevated=$elevated"
Add-Line "scriptPid=$PID"

# 旧实例还占着 AppX 里的文件，不先结束就部署不进去新程序集。
Get-Process -Name ChoyeonTools -ErrorAction SilentlyContinue | ForEach-Object {
    Add-Line "killing=$($_.Id)"
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

# 打包应用不能直接跑 AppX 里的 exe（没有激活上下文，进程会立刻退出），要走 winapp 的注册激活路径。
# 只有 winapp run 会重建并部署 AppX 布局，单独 dotnet build 不会刷新它。
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
$winapp = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winapp.exe'
$launcher = Start-Process -FilePath $winapp -ArgumentList 'run' `
    -WorkingDirectory (Join-Path $Repo 'ChoyeonTools') -PassThru -WindowStyle Hidden
Add-Line "launcherPid=$($launcher.Id)"

$deadline = (Get-Date).AddSeconds(150)
$handle = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 1000
    $alive = Get-Process -Name ChoyeonTools -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $alive) {
        continue
    }
    Add-Line "appPid=$($alive.Id)"
    if ($alive.MainWindowHandle -ne 0) {
        $handle = $alive.MainWindowHandle
        break
    }
}
Add-Line "mainwindow=$handle"

if ($handle -ne [IntPtr]::Zero) {
    # 首轮采集要几秒，管理员下驱动探针的 Open 更慢，界面会等它就绪后自动补采一次
    Start-Sleep -Seconds 40
    & (Join-Path $Repo 'tools\uia-pages.ps1') | Out-Null
    Add-Line 'dump=requested'
    & (Join-Path $Repo 'tools\capture-window.ps1') -Out (Join-Path $Artifacts 'window-elevated.png') | Out-Null
    Add-Line "capture=$(Join-Path $Artifacts 'window-elevated.png')"
}

Add-Line "pageFile=$(Join-Path $Artifacts 'uia-pages.txt')"
[System.IO.File]::WriteAllLines($Log, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "log=$Log"
