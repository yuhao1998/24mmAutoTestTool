#Requires -Version 5.1
<#
.SYNOPSIS
  步骤 3：SOC 升级（通过 USB 后门切 dev 模式 + update_engine_client）

.DESCRIPTION
  1. 校验 OTA 升级包（须含 payload.bin、payload_properties.txt）
  2. 若 adb 可用：adb root，推送 update.zip 至 /data/ota/ 并解压
  3. 若 adb 不可用：经串口 COM4@115200 切 peripheral 模式后再 adb root
  4. 调用 update_engine_client 升级，全程采集 update_engine 日志
  5. 安装完成后 PC 侧 adb reboot，再监听启动状态并输出版本信息

.PARAMETER PackagePath
  OTA 升级包路径（.zip 或已含 payload 的目录）

.PARAMETER SerialPort
  串口号，默认 COM4

.PARAMETER BaudRate
  串口波特率，默认 115200

.PARAMETER LogDir
  本地日志输出目录，默认脚本同目录下 logs/

.EXAMPLE
  .\soc_ota_upgrade.ps1 -PackagePath "D:\ota\T2_ProCV_20250609.zip"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$PackagePath,

    [string]$SerialPort = "COM4",
    [int]$BaudRate = 115200,
    [string]$LogDir = "",
    [int]$BootTimeoutSec = 600,
    [int]$UpdateTimeoutSec = 3600,
    [switch]$SkipEnableVerity
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $ScriptDir "logs"
}

$RemoteOtaDir = "/data/ota"
$UsbModeCmd = "echo peripheral > /sys/bus/platform/devices/a600000.ssusb/mode"
$RequiredEntries = @("payload.bin", "payload_properties.txt")

function Write-Step([string]$Message) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host "[$ts] $Message"
}

function Write-Err([string]$Message) {
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

function Ensure-LogDir {
    if (-not (Test-Path $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }
}

function Invoke-Adb {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$AdbArgs,
        [switch]$AllowFailure
    )
    $output = & adb @AdbArgs 2>&1
    $code = $LASTEXITCODE
    if (-not $AllowFailure -and $code -ne 0) {
        throw "adb $($AdbArgs -join ' ') failed (exit=$code): $output"
    }
    return ,@($output)
}

function Test-AdbInPath {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw "未找到 adb，请将其加入 PATH 后重试"
    }
}

function Test-AdbDeviceReady {
    $lines = Invoke-Adb -AllowFailure -AdbArgs @("devices")
    foreach ($line in $lines) {
        if ($line -match '^\S+\s+device$') {
            return $true
        }
    }
    return $false
}

function Restart-AdbServer {
    Write-Step "重置 adb server（kill-server / start-server）..."
    Invoke-Adb -AllowFailure -AdbArgs @("kill-server") | Out-Null
    Start-Sleep -Milliseconds 800
    Invoke-Adb -AllowFailure -AdbArgs @("start-server") | Out-Null
    Start-Sleep -Milliseconds 500
}

function Test-AdbShellUsable {
    $out = (Invoke-Adb -AllowFailure -AdbArgs @("shell", "echo", "OK") | Out-String)
    return ($out -match "OK")
}

function Wait-AdbDevice {
    param([int]$TimeoutSec = 120)
    Write-Step "等待 adb 设备就绪（最多 ${TimeoutSec}s，需 device + shell 可用）..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $round = 0
    while ((Get-Date) -lt $deadline) {
        $round++
        if (Test-AdbDeviceReady) {
            if (Test-AdbShellUsable) {
                Write-Step "adb 设备已连接且 shell 可用"
                return $true
            }
            if ($round -eq 1 -or ($round % 5) -eq 0) {
                Write-Step "adb 已列出 device，但 shell 尚未可用，继续等待..."
            }
        }
        elseif ($round -eq 1 -or ($round % 5) -eq 0) {
            Write-Step "等待 adb devices 出现 device 状态..."
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Invoke-OtaReboot {
    Write-Step "OTA 安装已完成，PC 侧执行 adb reboot 以激活 A/B 槽位切换 ..."
    Write-Step "说明: reboot 后 PC 侧仅被动监听 adb/串口，不 kill-server、不提前切 dev"
    Invoke-Adb -AllowFailure -AdbArgs @("reboot") | Out-Null
}

function Wait-SerialResumeThenWarmup {
    param(
        [int]$WarmupSec = 150,
        [int]$ResumeTimeoutSec = 600
    )
    Write-Step "被动监听串口 ${SerialPort}，等待通讯恢复（最多 ${ResumeTimeoutSec}s）..."
    Add-Type -AssemblyName System.IO.Ports
    $sb = New-Object System.Text.StringBuilder
    $resumeAt = $null
    try {
        $port = New-Object System.IO.Ports.SerialPort
        $port.PortName = $SerialPort
        $port.BaudRate = $BaudRate
        $port.Parity = [System.IO.Ports.Parity]::None
        $port.DataBits = 8
        $port.StopBits = [System.IO.Ports.StopBits]::One
        $port.ReadTimeout = 3000
        $port.WriteTimeout = 3000
        $port.Open()

        $resumeDeadline = (Get-Date).AddSeconds($ResumeTimeoutSec)
        while (-not $resumeAt -and (Get-Date) -lt $resumeDeadline) {
            if ($port.BytesToRead -gt 0) {
                [void]$sb.Append($port.ReadExisting())
                $resumeAt = Get-Date
                Write-Step "串口 ${SerialPort} 通讯已恢复"
                Write-Step "通讯恢复后等待 ${WarmupSec}s 再执行后续操作..."
            }
            Start-Sleep -Milliseconds 200
        }

        if (-not $resumeAt) {
            Write-Step "未在 ${ResumeTimeoutSec}s 内检测到串口数据，仍等待 ${WarmupSec}s 后继续..."
            $resumeAt = Get-Date
        }

        $warmupEnd = $resumeAt.AddSeconds($WarmupSec)
        while ((Get-Date) -lt $warmupEnd) {
            if ($port.BytesToRead -gt 0) {
                [void]$sb.Append($port.ReadExisting())
            }
            Start-Sleep -Milliseconds 200
        }
        if ($port.BytesToRead -gt 0) {
            [void]$sb.Append($port.ReadExisting())
        }
    }
    catch {
        Write-Step "串口被动监听失败（可忽略）: $($_.Exception.Message)"
    }
    finally {
        if ($port -and $port.IsOpen) { $port.Close() }
        if ($port) { $port.Dispose() }
    }

    $text = $sb.ToString()
    if ($text.Trim()) {
        $snippet = $text.Replace("`r", ' ').Replace("`n", ' ').Trim()
        if ($snippet.Length -gt 300) { $snippet = $snippet.Substring(0, 300) + "..." }
        Write-Step "串口监听 摘要: $snippet"
    }
    return $text
}

function Wait-AfterRebootWithDevMode {
    param([int]$TimeoutSec = $BootTimeoutSec)
    $serialWarmupSec = 150
    Write-Step "等待车机 reboot 并完成启动..."
    Write-Step "以 adb 断开作为车机开始 reboot 的标识，被动等待..."
    $disconnectDeadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $disconnectDeadline) {
        if (-not (Test-AdbDeviceReady)) {
            Write-Step "检测到 adb 已断开（reboot 已开始）"
            break
        }
        Start-Sleep -Seconds 2
    }

    Write-Step "reboot 已开始，被动监听串口 ${SerialPort} 直至通讯恢复，恢复后再等待 ${serialWarmupSec}s ..."
    Wait-SerialResumeThenWarmup -WarmupSec $serialWarmupSec -ResumeTimeoutSec $TimeoutSec | Out-Null
    Write-Step "${serialWarmupSec}s 被动等待完成，开始经串口切换 dev 并连接 adb 进行槽位/版本校验 ..."

    Switch-ToDevModeViaSerial -SkipAdbRestart

    Write-Step "等待 adb 在 dev 模式下重新连接..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $round = 0
    while ((Get-Date) -lt $deadline) {
        $round++
        if (Test-AdbDeviceReady) {
            if (Test-AdbShellUsable) {
                Write-Step "adb 设备已连接且 shell 可用"
                break
            }
        }
        if ($round -gt 1 -and ($round % 6) -eq 0) {
            Write-Step "adb 仍未连接，经串口 su → start adbd → 切换 USB peripheral（dev）模式 ..."
            Switch-ToDevModeViaSerial -SkipAdbRestart
        }
        elseif ($round -eq 1 -or ($round % 5) -eq 0) {
            Write-Step "等待 adb devices 出现 device 状态..."
        }
        Start-Sleep -Seconds 5
    }
    if (-not (Test-AdbShellUsable)) {
        throw "重启后 adb 长时间未连接，请检查 USB/串口及 dev 模式"
    }

    Write-Step "========== 升级后 A/B 槽位检查 =========="
    $slotSuffix = (Invoke-Adb -AllowFailure -AdbArgs @("shell", "getprop", "ro.boot.slot_suffix") | Out-String).Trim()
    Write-Step "  adb shell getprop ro.boot.slot_suffix = $slotSuffix"
    Write-Step "=========================================="

    Write-Step "等待系统启动完成（sys.boot_completed=1）..."
    $bootDeadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $bootDeadline) {
        $prop = (Invoke-Adb -AllowFailure -AdbArgs @("shell", "getprop", "sys.boot_completed") | Out-String).Trim()
        if ($prop -eq "1") {
            Start-Sleep -Seconds 5
            Write-Step "系统 boot 完成"
            return $true
        }
        Start-Sleep -Seconds 3
    }
    throw "等待 boot 完成超时（${TimeoutSec}s）"
}

function Wait-BootCompleted {
    param([int]$TimeoutSec = $BootTimeoutSec)
    Wait-AfterRebootWithDevMode -TimeoutSec $TimeoutSec
}

function Send-SerialCommand {
    param(
        [string]$Command,
        [int]$WaitAfterSec = 8
    )
    Add-Type -AssemblyName System.IO.Ports
    $port = New-Object System.IO.Ports.SerialPort
    $port.PortName = $SerialPort
    $port.BaudRate = $BaudRate
    $port.Parity = [System.IO.Ports.Parity]::None
    $port.DataBits = 8
    $port.StopBits = [System.IO.Ports.StopBits]::One
    $port.ReadTimeout = 3000
    $port.WriteTimeout = 3000
    try {
        Write-Step "打开串口 ${SerialPort}@${BaudRate}，发送: $Command"
        $port.Open()
        Start-Sleep -Seconds 1
        $port.WriteLine("")
        Start-Sleep -Milliseconds 500
        $port.WriteLine($Command)
        Start-Sleep -Seconds $WaitAfterSec
        if ($port.BytesToRead -gt 0) {
            $resp = $port.ReadExisting()
            if ($resp.Trim()) {
                Write-Step "串口响应: $($resp.Trim())"
            }
        }
    }
    finally {
        if ($port.IsOpen) { $port.Close() }
        $port.Dispose()
    }
}

function Switch-ToDevModeViaSerial {
    param([switch]$SkipAdbRestart)

    Write-Step "串口切 adb：su → start adbd → 切换 USB peripheral ..."
    # 固定三步：1) su  2) start adbd  3) echo peripheral ...
    Send-SerialSuAndCommands -Commands @("start adbd", $UsbModeCmd) -WaitAfterSecs @(3, 10)
    if ($SkipAdbRestart) {
        Write-Step "串口切 dev 完成，校验 adb 状态（不重置 adb server）..."
    }
    else {
        Restart-AdbServer
    }
    if (-not (Wait-AdbDevice -TimeoutSec 120)) {
        throw "串口切 dev 模式后 adb 仍不可用，请检查 USB/串口连接"
    }
}

function Send-SerialSuAndCommand {
    param(
        [string]$Command,
        [int]$WaitAfterSec = 8
    )
    Send-SerialSuAndCommands -Commands @($Command) -WaitAfterSecs @($WaitAfterSec)
}

function Send-SerialSuAndCommands {
    param(
        [string[]]$Commands,
        [int[]]$WaitAfterSecs = @()
    )
    Add-Type -AssemblyName System.IO.Ports
    $port = New-Object System.IO.Ports.SerialPort
    $port.PortName = $SerialPort
    $port.BaudRate = $BaudRate
    $port.Parity = [System.IO.Ports.Parity]::None
    $port.DataBits = 8
    $port.StopBits = [System.IO.Ports.StopBits]::One
    $port.ReadTimeout = 3000
    $port.WriteTimeout = 3000
    try {
        Write-Step "打开串口 ${SerialPort}@${BaudRate}，发送 su 进入 root ..."
        $port.Open()
        Start-Sleep -Milliseconds 500
        $port.WriteLine("")
        Start-Sleep -Milliseconds 500
        $port.WriteLine("su")
        Start-Sleep -Seconds 2
        if ($port.BytesToRead -gt 0) {
            $resp = $port.ReadExisting()
            if ($resp.Trim()) { Write-Step "su 响应: $($resp.Trim())" }
        }
        for ($i = 0; $i -lt $Commands.Count; $i++) {
            $cmd = $Commands[$i]
            if ([string]::IsNullOrWhiteSpace($cmd)) { continue }
            $waitSec = if ($i -lt $WaitAfterSecs.Count) { [int]$WaitAfterSecs[$i] } else { 8 }
            if ($waitSec -lt 1) { $waitSec = 1 }
            Write-Step "串口发送: $cmd"
            $port.WriteLine($cmd)
            Start-Sleep -Seconds $waitSec
            if ($port.BytesToRead -gt 0) {
                $resp = $port.ReadExisting()
                if ($resp.Trim()) { Write-Step "串口响应: $($resp.Trim())" }
            }
        }
    }
    finally {
        if ($port.IsOpen) { $port.Close() }
        $port.Dispose()
    }
}

function Enable-AdbRoot {
    Write-Step "执行 adb root ..."
    Invoke-Adb -AllowFailure -AdbArgs @("root") | Out-Null
    Start-Sleep -Seconds 3
    Invoke-Adb -AllowFailure -AdbArgs @("wait-for-device") | Out-Null

    $rootCheck = (Invoke-Adb -AllowFailure -AdbArgs @("shell", "id") | Out-String).Trim()
    if ($rootCheck -match "uid=0") {
        Write-Step "adb root 成功"
        return
    }
    throw "adb root 失败，请确认车机处于 dev 模式且允许 root"
}

function Test-OtaPackage {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "升级包不存在: $Path"
    }

    $item = Get-Item $Path
    if ($item.PSIsContainer) {
        $payload = Join-Path $Path "payload.bin"
        $props = Join-Path $Path "payload_properties.txt"
        if (-not (Test-Path $payload)) { throw "目录内缺少 payload.bin: $Path" }
        if (-not (Test-Path $props)) { throw "目录内缺少 payload_properties.txt: $Path" }
        return @{
            Type = "Directory"
            SourcePath = $Path
        }
    }

    if ($item.Extension -ne ".zip") {
        throw "升级包须为 .zip 或含 payload 的目录: $Path"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = @($zip.Entries | ForEach-Object {
            ($_.FullName -replace '\\', '/').TrimEnd('/')
        })
        $hasPayload = $false
        $hasProps = $false
        foreach ($name in $names) {
            $base = Split-Path $name -Leaf
            if ($base -eq "payload.bin") { $hasPayload = $true }
            if ($base -eq "payload_properties.txt") { $hasProps = $true }
        }
        if (-not $hasPayload) { throw "zip 内缺少 payload.bin: $Path" }
        if (-not $hasProps) { throw "zip 内缺少 payload_properties.txt: $Path" }
    }
    finally {
        $zip.Dispose()
    }

    return @{
        Type = "Zip"
        SourcePath = $Path
    }
}

function New-LocalUpdateZip {
    param(
        [hashtable]$PackageInfo,
        [string]$WorkDir
    )

    $updateZip = Join-Path $WorkDir "update.zip"
    if (Test-Path $updateZip) { Remove-Item $updateZip -Force }

    if ($PackageInfo.Type -eq "Zip") {
        Copy-Item -Path $PackageInfo.SourcePath -Destination $updateZip -Force
    }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($PackageInfo.SourcePath, $updateZip)
    }

    Write-Step "本地 update.zip 已准备: $updateZip"
    return $updateZip
}

function Deploy-UpdateScript {
    param([string]$WorkDir)

    $remoteScript = "${RemoteOtaDir}/run_soc_update.sh"
    $localScript = Join-Path $WorkDir "run_soc_update.sh"

    Write-Step "修复 payload_properties.txt 换行符 (CRLF -> LF) ..."
    $sedCmd = ('sed -i ''s/\r$//'' {0}/payload_properties.txt 2>/dev/null; true' -f $RemoteOtaDir)
    Invoke-Adb -AllowFailure -AdbArgs @('shell', $sedCmd) | Out-Null

    $props = (Invoke-Adb -AllowFailure -AdbArgs @('shell', "cd ${RemoteOtaDir} && cat payload_properties.txt") | Out-String).Trim()
    if (-not $props -or $props -notmatch 'FILE_HASH=' -or $props -notmatch 'FILE_SIZE=') {
        throw "车机端 payload_properties.txt 内容异常"
    }
    Write-Step "车机 payload_properties.txt 校验通过"

    $script = @"
# 在 adb shell 中先执行: cd $RemoteOtaDir
PROP=`$(cat payload_properties.txt)
update_engine_client \
--update \
--payload=file://$RemoteOtaDir/payload.bin \
--headers="`$PROP"
"@
    $script = $script -replace "`r`n", "`n" -replace "`r", "`n"
    if (-not $script.EndsWith("`n")) { $script += "`n" }
    [System.IO.File]::WriteAllText($localScript, $script, [System.Text.UTF8Encoding]::new($false))

    Write-Step "推送备用手动脚本（Linux LF）-> $remoteScript"
    Invoke-Adb -AdbArgs @('push', $localScript, $remoteScript)
    $fixScriptCmd = ('sed -i ''s/\r$//'' {0} 2>/dev/null; chmod 644 {0}' -f $remoteScript)
    Invoke-Adb -AllowFailure -AdbArgs @('shell', $fixScriptCmd) | Out-Null
}

function Test-AsciiOnlyPath {
    param([string]$Path)
    foreach ($ch in $Path.ToCharArray()) {
        if ([int]$ch -gt 127) { return $false }
    }
    return $true
}

function Resolve-AsciiPushSource {
    param(
        [string]$LocalZip,
        [string]$AdbToolsDir = ""
    )

    $tempDir = Join-Path $env:TEMP "soc_ota_push"
    $tempZip = Join-Path $tempDir "update.zip"
    if ((Resolve-Path $LocalZip).Path -eq (Resolve-Path $tempZip -ErrorAction SilentlyContinue).Path) {
        return $LocalZip
    }

    $needsTemp = -not (Test-AsciiOnlyPath $LocalZip)
    if (-not $needsTemp -and $AdbToolsDir -and -not (Test-AsciiOnlyPath $AdbToolsDir)) {
        $needsTemp = $true
    }
    if (-not $needsTemp) {
        return $LocalZip
    }

    if (-not (Test-Path $tempDir)) {
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    }
    Copy-Item -Path $LocalZip -Destination $tempZip -Force
    Write-Step "adb push 使用 ASCII 临时路径（避免中文/空格路径导致推送异常）: $tempZip"
    return $tempZip
}

function Test-RemoteUpdateZip {
    param(
        [string]$RemoteZip,
        [long]$ExpectedSize
    )

    $sizeRaw = (Invoke-Adb -AllowFailure -AdbArgs @('shell', "stat -c %s ${RemoteZip} 2>/dev/null || wc -c < ${RemoteZip}") | Out-String).Trim()
    $sizeToken = ($sizeRaw -split '\s+')[0]
    if (-not $sizeToken -or $sizeToken -notmatch '^\d+$') {
        throw "无法获取车机端 update.zip 大小: $sizeRaw"
    }
    $remoteSize = [long]$sizeToken
    if ($remoteSize -ne $ExpectedSize) {
        throw "车机端 update.zip 大小不匹配: 本地=$ExpectedSize 远端=$remoteSize ($RemoteZip)"
    }
    Write-Step "车机端 update.zip 校验通过 ($remoteSize 字节): $RemoteZip"
}

function Push-AndExtractOta {
    param(
        [string]$LocalUpdateZip,
        [string]$WorkDir
    )

    $adbToolsDir = Split-Path -Parent (Get-Command adb -ErrorAction Stop).Source
    $pushSource = Resolve-AsciiPushSource -LocalZip $LocalUpdateZip -AdbToolsDir $adbToolsDir

    Write-Step "推送 update.zip 到 ${RemoteOtaDir}/ ..."
    Invoke-Adb -AllowFailure -AdbArgs @('shell', "mkdir -p ${RemoteOtaDir}") | Out-Null
    Invoke-Adb -AdbArgs @('push', $pushSource, "${RemoteOtaDir}/")
    $listing = (Invoke-Adb -AllowFailure -AdbArgs @('shell', "ls -1 ${RemoteOtaDir} 2>/dev/null") | Out-String).Trim()
    if ($listing -match '\bupda\b' -and $listing -notmatch 'update\.zip') {
        throw "车机端推送文件名异常（发现 upda 而非 update.zip），请将工具部署到纯 ASCII 路径"
    }

    Write-Step "关闭 SELinux 强制模式 (setenforce 0) ..."
    Invoke-Adb -AllowFailure -AdbArgs @('shell', 'setenforce 0') | Out-Null
    $enforce = (Invoke-Adb -AllowFailure -AdbArgs @('shell', 'getenforce') | Out-String).Trim()
    Write-Step "当前 SELinux 状态: $enforce"

    Write-Step "解压 update.zip ..."
    $unzipCmd = ('cd {0} && (unzip -o update.zip || busybox unzip -o update.zip)' -f $RemoteOtaDir)
    Invoke-Adb -AllowFailure -AdbArgs @('shell', $unzipCmd) | Out-Null

    $check = (Invoke-Adb -AllowFailure -AdbArgs @("shell", "ls ${RemoteOtaDir}/payload.bin ${RemoteOtaDir}/payload_properties.txt 2>/dev/null") | Out-String).Trim()
    if ($check -notmatch "payload.bin" -or $check -notmatch "payload_properties.txt") {
        throw "解压后未在 ${RemoteOtaDir} 找到 payload.bin / payload_properties.txt"
    }
    Write-Step "OTA 包已就绪于 ${RemoteOtaDir}"
}

function Start-UpdateEngineLogCapture {
    param([string]$LogFile)
    Write-Step "开始采集 update_engine 日志 -> $LogFile"
    $proc = Start-Process -FilePath "adb" `
        -ArgumentList @("logcat", "-v", "time", "-s", "update_engine:*", "UpdateEngine:*", "update_engine_client:*") `
        -RedirectStandardOutput $LogFile `
        -RedirectStandardError "${LogFile}.err" `
        -PassThru `
        -WindowStyle Hidden
    Start-Sleep -Seconds 2
    return $proc
}

function Stop-LogCapture {
    param($Process)
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

function Start-SocUpdate {
    param([string]$UpgradeLogFile)

    $shellCmd = "cd ${RemoteOtaDir} && PROP=`$(cat payload_properties.txt) && update_engine_client --update --payload=file://${RemoteOtaDir}/payload.bin --headers=`"`$PROP`""
    Write-Step "启动 update_engine_client（adb shell 内执行，Linux 换行/字符）..."

    $proc = Start-Process -FilePath "adb" `
        -ArgumentList @("shell", $shellCmd) `
        -RedirectStandardOutput $UpgradeLogFile `
        -RedirectStandardError "${UpgradeLogFile}.err" `
        -PassThru `
        -NoNewWindow

    return $proc
}

function Test-UpdateSucceeded {
    param([string]$Text)
    $successPatterns = @(
        "UPDATED_NEED_REBOOT",
        "Update successfully applied",
        "already applied, waiting for reboot",
        "applied, waiting for reboot",
        "payload_application_complete.*error_code=0",
        "onPayloadApplicationComplete.*0"
    )
    foreach ($p in $successPatterns) {
        if ($Text -match $p) { return $true }
    }
    return $false
}

function Test-UpdateFailed {
    param([string]$Text)
    if (Test-UpdateSucceeded -Text $Text) { return $false }
    $failPatterns = @(
        "payload_application_complete.*error_code=[1-9]",
        "Aborting processing due to failure",
        "UPDATE_STATUS_REPORTING_ERROR",
        "Update failed"
    )
    foreach ($p in $failPatterns) {
        if ($Text -match $p) { return $true }
    }
    return $false
}

function Wait-UpdateComplete {
    param(
        $UpdateProcess,
        [string]$EngineLogFile,
        [int]$TimeoutSec = $UpdateTimeoutSec,
        [int]$RebootDelaySec = 20
    )

    Write-Step "监听升级进度（超时 ${TimeoutSec}s）..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $start = Get-Date
    $clientEnded = $false
    $round = 0

    while ((Get-Date) -lt $deadline) {
        $round++
        if (Test-Path $EngineLogFile) {
            $tail = Get-Content $EngineLogFile -Tail 200 -ErrorAction SilentlyContinue
            $text = ($tail -join "`n")
            if (Test-UpdateFailed -Text $text) {
                throw "升级失败，详见日志: $EngineLogFile"
            }
            if (Test-UpdateSucceeded -Text $text) {
                Write-Step "检测到安装完成信号，随后 PC 侧将执行 adb reboot 激活升级 ..."
                return
            }
        }

        if (-not $clientEnded -and $UpdateProcess.HasExited) {
            $clientEnded = $true
            $allText = ""
            if (Test-Path $EngineLogFile) {
                $allText = (Get-Content $EngineLogFile -Raw -ErrorAction SilentlyContinue)
            }
            if ($UpdateProcess.ExitCode -ne 0 -and -not (Test-UpdateSucceeded -Text $allText)) {
                throw "update_engine_client 异常退出 (exit=$($UpdateProcess.ExitCode))"
            }
            Write-Step "update_engine_client 已退出，继续监听 update_engine 日志直至完成或超时..."
        }

        if ($round -eq 1 -or ($round % 12) -eq 0) {
            $elapsed = [int]((Get-Date) - $start).TotalSeconds
            Write-Step "升级监听中（已等待 ${elapsed}s）..."
        }

        Start-Sleep -Seconds 2
    }

    if (Test-Path $EngineLogFile) {
        $finalText = (Get-Content $EngineLogFile -Raw -ErrorAction SilentlyContinue)
        if (Test-UpdateSucceeded -Text $finalText) {
            Write-Step "检测到安装完成信号，随后 PC 侧将执行 adb reboot 激活升级 ..."
            return
        }
    }

    throw "升级超时（${TimeoutSec}s），详见: $EngineLogFile"
}

function Get-DeviceVersionInfo {
    $props = @(
        "ro.build.display.id",
        "ro.build.version.incremental",
        "ro.build.fingerprint",
        "ro.vendor.build.version",
        "ro.product.build.version",
        "ro.build.description"
    )
    Write-Step "========== 车机版本信息 =========="
    foreach ($p in $props) {
        $val = (Invoke-Adb -AllowFailure -AdbArgs @("shell", "getprop", $p) | Out-String).Trim()
        if ($val) {
            Write-Host ("  {0} = {1}" -f $p, $val)
        }
    }
    Write-Step "=================================="
}

function Read-PackagePathInteractive {
    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
        return (Resolve-Path $PackagePath).Path
    }
    do {
        $inputPath = Read-Host "请输入 OTA 升级包路径（.zip 或目录）"
        $inputPath = $inputPath.Trim('"')
    } while ([string]::IsNullOrWhiteSpace($inputPath))
    return (Resolve-Path $inputPath).Path
}

# -------------------- main --------------------
$sessionTag = Get-Date -Format "yyyyMMdd_HHmmss"
$sessionLogDir = Join-Path $LogDir $sessionTag
Ensure-LogDir
New-Item -ItemType Directory -Path $sessionLogDir -Force | Out-Null

$engineLog = Join-Path $sessionLogDir "update_engine.log"
$clientLog = Join-Path $sessionLogDir "update_engine_client.log"
$workDir = Join-Path $sessionLogDir "work"
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

$logcatProc = $null
$updateProc = $null

try {
    Write-Step "=== 丰田T2版本测试工具 开始 ==="
    Write-Step "日志目录: $sessionLogDir"

    Test-AdbInPath
    $resolvedPackage = Read-PackagePathInteractive
    $packageInfo = Test-OtaPackage -Path $resolvedPackage
    Write-Step "升级包校验通过: $resolvedPackage"

    if (Test-AdbDeviceReady) {
        Write-Step "检测到 adb 已连接（dev 模式）"
    }
    else {
        Write-Step "adb 未连接，经串口切换 USB peripheral（dev）模式 ..."
        Switch-ToDevModeViaSerial
    }

    Enable-AdbRoot

    $localZip = New-LocalUpdateZip -PackageInfo $packageInfo -WorkDir $workDir
    Push-AndExtractOta -LocalUpdateZip $localZip -WorkDir $workDir

    if (-not (Wait-AdbDevice -TimeoutSec 60)) {
        throw "启动 logcat 前 adb 不可用，请确认 dev 模式已连接"
    }
    $logcatProc = Start-UpdateEngineLogCapture -LogFile $engineLog
    $updateProc = Start-SocUpdate -UpgradeLogFile $clientLog
    Wait-UpdateComplete -UpdateProcess $updateProc -EngineLogFile $engineLog

    Write-Step "=== OTA 安装完成（update_engine 已成功应用）==="
    Invoke-OtaReboot
    Wait-AfterRebootWithDevMode

    Get-DeviceVersionInfo
    Write-Step "=== 丰田T2版本测试工具 完成 ==="
    exit 0
}
catch {
    Write-Err $_.Exception.Message
    Write-Step "=== 丰田T2版本测试工具 失败 ==="
    Write-Step "完整日志目录: $sessionLogDir"
    exit 1
}
finally {
    Stop-LogCapture -Process $logcatProc
    if ($null -ne $updateProc -and -not $updateProc.HasExited) {
        Stop-Process -Id $updateProc.Id -Force -ErrorAction SilentlyContinue
    }
}
