Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
$Repo = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Repo '.artifacts'
$null = New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

$out = Join-Path $Artifacts 'uia-overview.txt'
$sb = New-Object System.Text.StringBuilder

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Choyeon Tools')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) {
  [System.IO.File]::WriteAllText($out, 'WINDOW NOT FOUND', [System.Text.Encoding]::UTF8)
  return
}

$li = [System.Windows.Automation.ControlType]::ListItem
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $li)))
foreach ($item in $items) {
  if ($item.Current.Name -eq '总览') {
    try {
      $pat = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
      $pat.Select()
    } catch {}
    break
  }
}
Start-Sleep -Milliseconds 1500

$all0 = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($el in $all0) {
  try {
    $sp = $el.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, 100) }
  } catch {}
}
Start-Sleep -Milliseconds 1200

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($t in $all) {
  $ct = $t.Current.ControlType
  if ($ct -eq [System.Windows.Automation.ControlType]::Text -or $ct -eq [System.Windows.Automation.ControlType]::Hyperlink -or $ct -eq [System.Windows.Automation.ControlType]::Button) {
    $n = $t.Current.Name
    if ($null -ne $n -and $n.Trim().Length -gt 0) { [void]$sb.AppendLine($ct.ProgrammaticName + ' | ' + $n.Trim()) }
  }
}

[System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.Encoding]::UTF8)
& (Join-Path $Repo 'tools\capture-window.ps1') -Out (Join-Path $Artifacts 'window-overview.png') | Out-Null
Write-Output ('wrote ' + $out)