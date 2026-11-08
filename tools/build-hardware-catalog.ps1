param(
    [string]$Source = 'tools\vendor-data\pci.ids',
    [string]$Out = 'ChoyeonTools\Assets\Hardware\pci.json'
)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Source)) { $Source = Join-Path $Repo $Source }
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $Repo $Out }

# pci.ids 语法：行首四位十六进制 = 厂商；单个制表符 = 该厂商下的设备；两个制表符 = 子系统（板卡厂商与型号）；
# 文件末尾 C 段 = 类代码，本程序没有可查类代码的设备 ID，读到 C 段即停止。
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
$subsystems = [ordered]@{}
$currentVendor = ''
$currentDevice = ''
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
        if ($line.Length -lt 6) { $skipped++; continue }
        if ($line[1] -eq "`t") {
            # 子系统行：ven:dev:subven:subdev → 板卡名称；子系统厂商号本身也是厂商表里的 PCI 厂商。
            if ($line.Substring(2) -match '^([0-9a-f]{4})\s+([0-9a-f]{4})\s+(.+)$' -and $currentDevice -ne '') {
                $key = $currentVendor + ':' + $currentDevice + ':' + $Matches[1] + ':' + $Matches[2]
                if (-not $subsystems.Contains($key)) { $subsystems[$key] = JsonEscape $Matches[3].Trim() }
            } else { $skipped++ }
            continue
        }

        $id = $line.Substring(1, 4)
        if ($id -notmatch '^[0-9a-f]{4}$') { $skipped++; continue }
        $currentDevice = $id
        $key = $currentVendor + ':' + $id
        if (-not $devices.Contains($key)) { $devices[$key] = JsonEscape $line.Substring(6).Trim() }
        continue
    }

    if ($line -match '^([0-9a-f]{4})\s+(.*)$') {
        $currentVendor = $Matches[1]
        $currentDevice = ''
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
[void]$sb.Append('"source":"PCI ID Repository (https://pci-ids.ucw.cz/)",')
[void]$sb.Append('"attribution":"Copyright Martin Mares and Albert Pool; distributed under GPL-2.0-or-later or BSD-3-Clause",')
[void]$sb.Append('"version":"' + (JsonEscape $version) + '",')
[void]$sb.Append('"vendors":{')
$script:PendingComma = $false
foreach ($key in $vendors.Keys) { EmitPair $sb $key $vendors[$key] }
[void]$sb.Append('},"devices":{')
$script:PendingComma = $false
foreach ($key in $devices.Keys) { EmitPair $sb $key $devices[$key] }
[void]$sb.Append('},"subsystems":{')
$script:PendingComma = $false
foreach ($key in $subsystems.Keys) { EmitPair $sb $key $subsystems[$key] }
[void]$sb.Append('}}')

[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

$info = Get-Item $Out
Write-Output ("version=$version vendors=$($vendors.Count) devices=$($devices.Count) subsystems=$($subsystems.Count) skipped=$skipped")
Write-Output ("out=$($info.FullName) bytes=$($info.Length)")
