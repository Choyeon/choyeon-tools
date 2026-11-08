Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
$Repo = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Repo '.artifacts'
$null = New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

$out = Join-Path $Artifacts 'uia-pages.txt'
$sb = New-Object System.Text.StringBuilder

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Choyeon Tools')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) {
  [void]$sb.AppendLine('WINDOW NOT FOUND')
  [System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.Encoding]::UTF8)
  return
}

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

function Dump-Page([string]$name) {
  $ok = Select-Page $name
  Start-Sleep -Milliseconds 1600
  [void]$sb.AppendLine('')
  [void]$sb.AppendLine('===== PAGE ' + $name + ' selected=' + $ok + ' =====')
  $texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
  foreach ($t in $texts) {
    $n = $t.Current.Name
    if ($null -ne $n -and $n.Trim().Length -gt 0) { [void]$sb.AppendLine($n.Trim()) }
  }
}

foreach ($page in @('设置', '内存', 'GPU', '声卡', '网络', 'USB', '系统', '硬盘', 'CPU', '显示器')) {
  Dump-Page $page
}

[System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.Encoding]::UTF8)
Write-Output ('wrote ' + $out)
