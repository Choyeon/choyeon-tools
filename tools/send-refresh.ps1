param([string]$ProcessName = 'ChoyeonTools')

Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Foreground -Name Api -MemberDefinition @'
[System.Runtime.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(IntPtr hWnd);
'@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($null -eq $proc) {
    Write-Error 'window not found'
    exit 1
}

[void][Foreground.Api]::SetForegroundWindow($proc.MainWindowHandle)
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait('^r')
Write-Output 'ctrl+r sent'
