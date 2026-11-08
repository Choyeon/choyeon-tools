Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Repo '.artifacts'
$null = New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
$log = New-Object System.Collections.Generic.List[string]
function Say([string]$text) {
  $log.Add($text)
  [IO.File]::WriteAllLines((Join-Path $Artifacts 'uia-actions.txt'), $log, [System.Text.UTF8Encoding]::new($false))
  Write-Host $text
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$textCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$buttonCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$windowCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)

# 主窗口按进程取“文本元素最多”的那个：同进程还有弹层窗口，按名字取会抓到空树。
$app = Get-Process ChoyeonTools -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $app) { Say 'APP PROCESS NOT FOUND'; return }
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$win = $null
$best = 0
foreach ($candidate in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
  $count = $candidate.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond).Count
  if ($count -gt $best) { $best = $count; $win = $candidate }
}
if ($null -eq $win) { Say 'WINDOW NOT FOUND'; return }
Say ('main window text elements: ' + $best)

function Find-ByName([string]$controlType, [string]$name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::$controlType)
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    if ($e.Current.Name -eq $name) { return $e }
  }
  return $null
}

function Invoke-Element($element, [string]$what) {
  if ($null -eq $element) { Say ("MISSING ELEMENT: " + $what); return }
  $ip = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $ip.Invoke()
  Say ("invoked: " + $what)
}

# 设置现在是导航底部的一页，不再是弹层；导航项在 UI 树里是 ListItem。
function Go-Page([string]$name) {
  $item = Find-ByName 'ListItem' $name
  if ($null -eq $item) { Say ('  nav item missing: ' + $name); return $false }
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 900
  Say ('  page: ' + $name)
  return $true
}

# ComboBox 的下拉项只有展开后才在 UI 树里出现，所以先展开再选。
function Select-Combo([string]$comboName, [string]$option) {
  $combo = Find-ByName 'ComboBox' $comboName
  if ($null -eq $combo) { Say ("combo missing: " + $comboName); return $false }
  $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
  $ec.Expand()
  Start-Sleep -Milliseconds 600
  $item = Find-ByName 'ListItem' $option
  if ($null -eq $item) { $ec.Collapse(); Say ("option missing: " + $option); return $false }
  $si = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $si.Select()
  Start-Sleep -Milliseconds 500
  Say ("selected " + $comboName + " = " + $option)
  return $true
}

function Status-Line {
  $lines = @()
  foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
    $n = $t.Current.Name
    if ($n -and ($n.StartsWith('上次更新') -or $n.StartsWith('正在采集') -or $n.StartsWith('已复制') -or $n.StartsWith('已导出') -or $n.StartsWith('已取消导出') -or $n.StartsWith('采集失败'))) { $lines += $n }
  }
  return ($lines -join ' | ')
}

function Report-Rect([string]$label, [string]$textName) {
  $el = Find-ByName 'Text' $textName
  if ($null -eq $el) { Say ("  " + $label + ": MISSING"); return }
  $r = $el.Current.BoundingRectangle
  Say ("  " + $label + ": x=" + [math]::Round($r.X) + " y=" + [math]::Round($r.Y) + " w=" + [math]::Round($r.Width) + " h=" + [math]::Round($r.Height))
}

Say '== 1. ACTIONS LIVE ONLY ON THE SETTINGS PAGE =='
Go-Page '总览' | Out-Null
foreach ($label in @('刷新', '复制本页', '导出 JSON')) {
  Say ('  button on overview: ' + $label + ' = ' + ($null -ne (Find-ByName 'Button' $label)))
}
Say ('  主题 combo on overview: ' + ($null -ne (Find-ByName 'ComboBox' '主题')))
Say ('  设置 nav item present: ' + ($null -ne (Find-ByName 'ListItem' '设置')))
Say ('  关于 nav item removed: ' + ($null -eq (Find-ByName 'ListItem' '关于')))

Say '== 2. METRIC CARD GEOMETRY (overview) =='
foreach ($label in @('CPU 使用率', '内存占用', '磁盘剩余', '运行时长')) { Report-Rect $label $label }

Say '== 3. SETTINGS PAGE CONTENT =='
Go-Page '设置' | Out-Null
foreach ($label in @('外观', '应用主题', '常规', '实时刷新', '数据', '重新采集', '复制到剪贴板', '导出 JSON', '关于', '程序信息', '版本')) {
  Say ('  text present: ' + $label + ' = ' + ($null -ne (Find-ByName 'Text' $label)))
}
foreach ($label in @('刷新', '复制本页', '导出 JSON')) {
  Say ('  button present: ' + $label + ' = ' + ($null -ne (Find-ByName 'Button' $label)))
}
Report-Rect 'about card' '程序信息'

Say '== 4. THEME =='
Select-Combo '主题' '浅色' | Out-Null
$settings = Join-Path $env:LOCALAPPDATA 'ChoyeonTools\settings.json'
foreach ($dir in @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue | Where-Object Name -like '8BF621AA*')) {
  $packed = Join-Path $dir.FullName 'LocalCache\Local\ChoyeonTools\settings.json'
  if (Test-Path $packed) { $settings = $packed }
}
Say ('  settings file: ' + $settings + ' exists=' + (Test-Path $settings))
if (Test-Path $settings) { Say ('  ' + ((Get-Content -Raw -Encoding UTF8 $settings) -replace "`r?`n", ' ')) }
Select-Combo '主题' '跟随系统' | Out-Null

Say '== 5. AUTO REFRESH =='
Select-Combo '自动刷新' '10 秒' | Out-Null
if (Test-Path $settings) { Say ('  ' + ((Get-Content -Raw -Encoding UTF8 $settings) -replace "`r?`n", ' ')) }
Select-Combo '自动刷新' '30 秒' | Out-Null

Say '== 6. COPY PAGE FROM SETTINGS =='
Invoke-Element (Find-ByName 'Button' '复制本页') '复制本页'
Start-Sleep -Seconds 2
$clip = Get-Clipboard -Raw
Say ('  clipboard chars: ' + $clip.Length)
Say ('  ' + (($clip -split "`r?`n" | Select-Object -First 4) -join "`n  "))
Say ('  ' + (Status-Line))

Say '== 7. EXPORT JSON PICKER THEN CANCEL =='
Invoke-Element (Find-ByName 'Button' '导出 JSON') '导出 JSON'
Start-Sleep -Seconds 5
$picked = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $windowCond)) {
  $n = $w.Current.Name
  if ($n -match '保存|另存|Save As') { $picked = $w; break }
}
if ($null -eq $picked) {
  Say '  picker window not detected; visible windows:'
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $windowCond)) {
    if ($w.Current.Name) { Say ('    ' + $w.Current.Name + ' (pid ' + $w.Current.ProcessId + ')') }
  }
} else {
  Say ('  picker window: ' + $picked.Current.Name + ' (pid ' + $picked.Current.ProcessId + ')')
  $cancel = $null
  foreach ($b in $picked.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond)) {
    if ($b.Current.Name -match '取消|Cancel') { $cancel = $b; break }
  }
  if ($null -eq $cancel) { Say '  cancel button not found' } else { Invoke-Element $cancel '取消' }
}
Start-Sleep -Seconds 2
Say ('  status after export: ' + (Status-Line))
Say '== DONE =='
