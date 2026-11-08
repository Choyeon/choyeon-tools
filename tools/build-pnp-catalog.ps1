param(
    [string]$Source = 'tools\vendor-data\pnp.ids',
    [string]$Out = 'ChoyeonTools\Assets\Hardware\pnp.json'
)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Source)) { $Source = Join-Path $Repo $Source }
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $Repo $Out }

# pnp.ids 语法：三字符 PNP 代码 + 制表符 + 厂商名，无版本头；版本用 hwdata 仓库快照日期。
function JsonEscape([string]$text) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $text.ToCharArray()) {
        switch ($ch) {
            '"' { [void]$sb.Append('\"') }
            '\' { [void]$sb.Append('\\') }
            "`t" { [void]$sb.Append('\t') }
            default {
                $code = [int]$ch
                if ($code -lt 32) { [void]$sb.AppendFormat('\u{0:x4}', $code) }
                else { [void]$sb.Append($ch) }
            }
        }
    }
    return $sb.ToString()
}

$entries = [ordered]@{}
$skipped = 0
foreach ($line in [System.IO.File]::ReadLines($Source)) {
    if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
    if ($line -match '^([A-Z]{3})\s+(.+)$') {
        $key = $Matches[1]
        if (-not $entries.Contains($key)) { $entries[$key] = JsonEscape $Matches[2].Trim() }
    } else { $skipped++ }
}

$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{')
[void]$sb.Append('"source":"hwdata pnp.ids (https://github.com/vcrhonek/hwdata)",')
[void]$sb.Append('"attribution":"Sourced from the UEFI PNP ID Registry (https://uefi.org/uefi-pnp-export); distributed under GPL-2.0-or-later or XFree86-1.0",')
[void]$sb.Append('"version":"2026-11-03",')
[void]$sb.Append('"entries":{')
$pending = $false
foreach ($key in $entries.Keys) {
    if ($pending) { [void]$sb.Append(',') }
    $pending = $true
    [void]$sb.Append('"').Append($key).Append('":"').Append($entries[$key]).Append('"')
}
[void]$sb.Append('}}')

[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

$info = Get-Item $Out
Write-Output ("entries=$($entries.Count) skipped=$skipped")
Write-Output ("out=$($info.FullName) bytes=$($info.Length)")