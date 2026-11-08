Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
$Repo = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Repo '.artifacts'
$null = New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

$out = Join-Path $Artifacts 'uia-dump.txt'
$sb = New-Object System.Text.StringBuilder

function Dump-Window($win) {
  [void]$sb.AppendLine("WINDOW: " + $win.Current.Name)
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  $texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  [void]$sb.AppendLine("TEXT ELEMENTS: " + $texts.Count)
  foreach ($t in $texts) {
    $n = $t.Current.Name
    if ($n -ne $null -and $n.Trim().Length -gt 0) { [void]$sb.AppendLine($n.Trim()) }
  }
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Choyeon Tools')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

if ($win -eq $null) {
  [void]$sb.AppendLine("WINDOW NOT FOUND")
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    [void]$sb.AppendLine("candidate: " + $w.Current.Name)
  }
} else {
  Dump-Window $win

  $navCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $navCond)
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("LISTITEMS: " + $items.Count)
  foreach ($i in $items) { [void]$sb.AppendLine("  item: " + $i.Current.Name) }

  foreach ($label in @('CPU','内存','GPU','硬盘','声卡','网络','系统','显示器','设置','总览')) {
    $pick = $null
    foreach ($i in $items) { if ($i.Current.Name -eq $label) { $pick = $i; break } }
    if ($pick -eq $null) { continue }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("######## SELECTED: " + $label)
    try {
      $sp = $pick.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
      $sp.Select()
      Start-Sleep -Milliseconds 600
      Dump-Window $win
    } catch {
      [void]$sb.AppendLine("SELECT FAILED: " + $_.Exception.Message)
    }
  }
}

[System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.Encoding]::UTF8)
Write-Output "written $out"
