Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
$Repo = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Repo '.artifacts'
$null = New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

$out = Join-Path $Artifacts 'uia-stability.txt'
$sb = New-Object System.Text.StringBuilder
function Say($m) { [void]$sb.AppendLine($m); Write-Host $m }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$app = Get-Process ChoyeonTools -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $app) {
  Say 'APP PROCESS NOT FOUND'
  [System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.Encoding]::UTF8)
  return
}

$AU = [System.Windows.Automation.AutomationElement]
$TextCond = New-Object System.Windows.Automation.PropertyCondition($AU::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$ButtonCond = New-Object System.Windows.Automation.PropertyCondition($AU::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$ListCond = New-Object System.Windows.Automation.PropertyCondition($AU::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$ComboCond = New-Object System.Windows.Automation.PropertyCondition($AU::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
$PidCond = New-Object System.Windows.Automation.PropertyCondition($AU::ProcessIdProperty, $app.Id)

# 主窗口按进程挑"文本元素最多"的那个，避免抓到同名但内容为空的残留窗口。
$win = $null
$best = 0
foreach ($c in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $PidCond)) {
  $count = $c.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TextCond).Count
  if ($count -gt $best) { $best = $count; $win = $c }
}
if ($null -eq $win) {
  Say 'WINDOW NOT FOUND'
  [System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.Encoding]::UTF8)
  return
}

function Get-TextMap {
  $map = @{}
  foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TextCond)) {
    $n = $t.Current.Name
    if ($null -eq $n -or $n.Trim().Length -eq 0) { continue }
    # Current.RuntimeId 在这里返回 null，用 GetRuntimeId() 作为元素身份。
    $map[($t.GetRuntimeId() -join ',')] = $n.Trim()
  }
  return , $map
}

function Test-Visible([string]$label) {
  foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TextCond)) {
    if ($t.Current.Name -eq $label) { return $true }
  }
  foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ButtonCond)) {
    if ($b.Current.Name -eq $label) { return $true }
  }
  foreach ($c in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ComboCond)) {
    if ($c.Current.Name -eq $label) { return $true }
  }
  return $false
}

function Select-NavItem([string]$label) {
  foreach ($i in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ListCond)) {
    if ($i.Current.Name -ne $label) { continue }
    $p = $i.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $p.Select()
    Start-Sleep -Milliseconds 800
    Say ('nav selected: ' + $label)
    return $true
  }
  Say ('NAV ITEM MISSING: ' + $label)
  return $false
}

function Set-ComboValue([string]$comboName, [string]$optionLabel) {
  foreach ($c in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ComboCond)) {
    if ($c.Current.Name -ne $comboName) { continue }
    $ec = $c.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand()
    Start-Sleep -Milliseconds 500
    foreach ($li in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ListCond)) {
      if ($li.Current.Name -ne $optionLabel) { continue }
      $si = $li.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
      $si.Select()
      Start-Sleep -Milliseconds 500
      Say ('combo ' + $comboName + ' -> ' + $optionLabel)
      return $true
    }
    $ec.Collapse()
    Say ('OPTION MISSING: ' + $optionLabel)
    return $false
  }
  Say ('COMBO MISSING: ' + $comboName)
  return $false
}

$script:lastSamples = @()
function Watch($phase, $seconds) {
  $samples = @()
  for ($i = 0; $i -lt [int]($seconds * 5); $i++) {
    $n = 0
    foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TextCond)) {
      if ($t.Current.Name -and $t.Current.Name.Trim().Length -gt 0) { $n++ }
    }
    $samples += $n
    Start-Sleep -Milliseconds 200
  }
  $script:lastSamples = $samples
  Say ('phase ' + $phase + ': samples=' + $samples.Count + ' min=' + (($samples | Measure-Object -Minimum).Minimum) + ' max=' + (($samples | Measure-Object -Maximum).Maximum))
}

Say '== 1. IDLE BASELINE (overview, ticker running) =='
$before = Get-TextMap
Say ('text elements: ' + $before.Count)
Watch 'idle-6s' 6

Say '== 2. OVERVIEW PAGE: settings-only controls must be absent =='
foreach ($label in @('刷新', '复制本页', '导出', '主题', '应用主题', '程序信息')) {
  Say ('  visible on overview: ' + $label + ' = ' + (Test-Visible $label))
}

Say '== 3. SETTINGS PAGE CONTENT =='
Select-NavItem '设置' | Out-Null
foreach ($label in @('外观', '常规', '数据', '关于', '应用主题', '实时刷新', '重新采集', '复制到剪贴板', '导出 JSON', '刷新', '复制本页', '导出', '主题', '自动刷新', '程序信息', '版本', '作者', '许可证')) {
  Say ('  present: ' + $label + ' = ' + (Test-Visible $label))
}
foreach ($kv in (Get-TextMap).Values) {
  if ($kv -match 'Choyeon Tools|^1\.0\.0|Choyeon|MIT|查看 CPU') { Say ('  about line: ' + $kv) }
}

Say '== 4. ARM 10s AUTO REFRESH, BACK TO OVERVIEW =='
Set-ComboValue '自动刷新' '10 秒' | Out-Null
Select-NavItem '总览' | Out-Null
$before = Get-TextMap
Start-Sleep -Seconds 2

Say '== 5. SAMPLING THROUGH TWO AUTO REFRESHES (26s) =='
Watch 'auto-refresh-26s' 26
Say ('lowest visible element count during refresh: ' + (($script:lastSamples | Measure-Object -Minimum).Minimum))

$after = Get-TextMap
$persisted = 0
foreach ($id in $before.Keys) { if ($after.ContainsKey($id)) { $persisted++ } }
Say ('surviving element ids: ' + $persisted + ' / ' + $before.Count)
$changed = 0
foreach ($id in $after.Keys) {
  if ($before.ContainsKey($id) -and $before[$id] -ne $after[$id]) {
    $changed++
    if ($changed -le 8) { Say ('value changed in place: ' + $before[$id] + ' -> ' + $after[$id]) }
  }
}
Say ('in-place text updates: ' + $changed)
foreach ($kv in $after.Values) {
  if ($kv -match '上次更新|采集失败') { Say ('status: ' + $kv) }
}

Say '== 6. RESTORE 30s =='
Select-NavItem '设置' | Out-Null
Set-ComboValue '自动刷新' '30 秒' | Out-Null
Select-NavItem '总览' | Out-Null

Say '== DONE =='
[System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.Encoding]::UTF8)
