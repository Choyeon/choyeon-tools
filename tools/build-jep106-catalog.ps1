param(
    [string]$Source = 'tools\vendor-data\jep106.ids',
    [string]$Out = 'ChoyeonTools\Assets\Hardware\jep106.json'
)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Source)) { $Source = Join-Path $Repo $Source }
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $Repo $Out }

# jep106.ids 语法：裸数字行 = 银行号；制表符行 = "条目号 厂商名"。键写成 "银行:条目"。
# 解码约定与 nwinfo 一致：SMBIOS 厂商 ID 高字节 & 0x7F + 1 = 银行，低字节 & 0x7F = 条目。
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

$version = ''
$entries = [ordered]@{}
$bank = ''
$skipped = 0
foreach ($line in [System.IO.File]::ReadLines($Source)) {
    if ($line.Length -eq 0) { continue }
    if ($line[0] -eq '#') {
        if ($line -match '^#\s*Version:\s*(.+)$') { $version = $Matches[1].Trim() }
        continue
    }

    if ($line -match '^\d+$') { $bank = $line.Trim(); continue }
    if ($line[0] -eq "`t" -and $bank -ne '' -and $line.Substring(1) -match '^(\d+)\s+(.+)$') {
        $key = $bank + ':' + $Matches[1]
        if (-not $entries.Contains($key)) { $entries[$key] = JsonEscape $Matches[2].Trim() }
    } else { $skipped++ }
}

$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{')
[void]$sb.Append('"source":"JEDEC JEP106 via nwinfo (https://github.com/a1ive/nwinfo)",')
[void]$sb.Append('"attribution":"Manufacturer codes are factual data from JEDEC JEP106 (https://www.jedec.org); table extracted by nwinfo (Unlicense)",')
[void]$sb.Append('"version":"' + (JsonEscape $version) + '",')
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
Write-Output ("version=$version entries=$($entries.Count) skipped=$skipped")
Write-Output ("out=$($info.FullName) bytes=$($info.Length)")