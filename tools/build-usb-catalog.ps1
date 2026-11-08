param(
    [string]$Source = 'tools\vendor-data\usb.ids',
    [string]$Out = 'ChoyeonTools\Assets\Hardware\usb.json'
)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Source)) { $Source = Join-Path $Repo $Source }
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $Repo $Out }

# usb.ids 语法与 pci.ids 一致：行首四位十六进制 = 厂商；单个制表符 = 该厂商下的产品；
# 两个制表符 = 接口（不入库）；文件末尾 C 段 = 类代码（Windows 的 USB 设备实例 ID 里没有类代码，也不入库）。
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
$vendors = [ordered]@{}
$devices = [ordered]@{}
$currentVendor = ''
$skipped = 0

foreach ($line in [System.IO.File]::ReadLines($Source)) {
    if ($line.Length -eq 0) { continue }
    if ($line[0] -eq '#') {
        if ($line -match '^#\s*Version:\s*(.+)$') { $version = $Matches[1].Trim() }
        continue
    }

    # C 段之后全是类代码，本程序用不到，直接停止读取。
    if ($line[0] -eq 'C') { break }

    if ($line[0] -eq "`t") {
        if ($line.Length -lt 6 -or $line[1] -eq "`t") { $skipped++; continue }
        $id = $line.Substring(1, 4)
        if ($id -notmatch '^[0-9a-f]{4}$') { $skipped++; continue }
        $key = $currentVendor + ':' + $id
        if (-not $devices.Contains($key)) { $devices[$key] = JsonEscape $line.Substring(6).Trim() }
        continue
    }

    if ($line -match '^([0-9a-f]{4})\s+(.*)$') {
        $currentVendor = $Matches[1]
        $vendors[$currentVendor] = JsonEscape $Matches[2].Trim()
    }
}

$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sb = New-Object System.Text.StringBuilder
$script:PendingComma = $false

function EmitPair([System.Text.StringBuilder]$target, [string]$key, [string]$value) {
    if ($script:PendingComma) { [void]$target.Append(',') }
    $script:PendingComma = $true
    [void]$target.Append('"').Append($key).Append('":"').Append($value).Append('"')
}

[void]$sb.Append('{')
[void]$sb.Append('"source":"USB ID Database (http://www.linux-usb.org/usb.ids)",')
[void]$sb.Append('"attribution":"Copyright Stephen J. Gowdy and contributors; distributed under GPL-2.0-or-later or BSD-3-Clause",')
[void]$sb.Append('"version":"' + (JsonEscape $version) + '",')
[void]$sb.Append('"vendors":{')
$script:PendingComma = $false
foreach ($key in $vendors.Keys) { EmitPair $sb $key $vendors[$key] }
[void]$sb.Append('},"devices":{')
$script:PendingComma = $false
foreach ($key in $devices.Keys) { EmitPair $sb $key $devices[$key] }
[void]$sb.Append('}}')

[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

$info = Get-Item $Out
Write-Output ("version=$version vendors=$($vendors.Count) devices=$($devices.Count) skipped=$skipped")
Write-Output ("out=$($info.FullName) bytes=$($info.Length)")
