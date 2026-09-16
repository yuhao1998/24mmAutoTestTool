//go:build windows

package main

import (
	"fmt"
	"os/exec"
	"strings"
	"time"
)

func sendSerialCommand(cfg Config, command string, waitAfter time.Duration) error {
	return sendSerialSuAndCommands(cfg, []string{command}, []time.Duration{waitAfter})
}

// sendSerialSuAndCommands: 1) su  2) 依次发送 commands
func sendSerialSuAndCommands(cfg Config, commands []string, waits []time.Duration) error {
	if len(commands) == 0 {
		return fmt.Errorf("commands 不能为空")
	}
	cmdLines := make([]string, 0, len(commands))
	for i, c := range commands {
		waitSec := 8
		if i < len(waits) {
			waitSec = int(waits[i].Seconds())
		}
		if waitSec < 1 {
			waitSec = 1
		}
		cmdLines = append(cmdLines, fmt.Sprintf(`
  Write-Output ('SEND:' + '%s')
  $p.WriteLine('%s')
  Start-Sleep -Seconds %d
  if ($p.BytesToRead -gt 0) { $p.ReadExisting() | Out-String | Write-Output }
`, escapePsSingle(c), escapePsSingle(c), waitSec))
	}

	psScript := fmt.Sprintf(`
Add-Type -AssemblyName System.IO.Ports
$p = New-Object System.IO.Ports.SerialPort '%s', %d, None, 8, One
$p.ReadTimeout = 3000
$p.WriteTimeout = 3000
try {
  $p.Open()
  Start-Sleep -Milliseconds 500
  $p.WriteLine('')
  Start-Sleep -Milliseconds 500
  Write-Output 'SEND:su'
  $p.WriteLine('su')
  Start-Sleep -Seconds 2
  if ($p.BytesToRead -gt 0) { $p.ReadExisting() | Out-String | Write-Output }
%s
} finally {
  if ($p.IsOpen) { $p.Close() }
  $p.Dispose()
}
`, escapePsSingle(cfg.SerialPort), cfg.BaudRate, strings.Join(cmdLines, "\n"))

	logStep(fmt.Sprintf("打开串口 %s@%d，先 su 再发送 %d 条命令", cfg.SerialPort, cfg.BaudRate, len(commands)))
	cmd := exec.Command("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", psScript)
	out, err := cmd.CombinedOutput()
	text := strings.TrimSpace(string(out))
	if err != nil {
		return fmt.Errorf("串口操作失败: %w: %s", err, text)
	}
	if text != "" {
		logStep("串口响应: " + text)
	}
	return nil
}

func escapePsSingle(s string) string {
	return strings.ReplaceAll(s, "'", "''")
}

func switchToDevModeViaSerial(cfg Config, runner AdbRunner) error {
	// 固定三步：su → start adbd → echo peripheral
	logStep("串口切 adb：su → start adbd → 切换 USB peripheral ...")
	if err := sendSerialSuAndCommands(cfg,
		[]string{"start adbd", cfg.UsbModeCmd},
		[]time.Duration{3 * time.Second, 10 * time.Second}); err != nil {
		return err
	}
	if !waitAdbDevice(runner, 120) {
		return fmt.Errorf("串口切 dev 模式后仍未检测到 adb 设备，请检查 USB/串口连接")
	}
	return nil
}

func waitSerialResumeThenWarmup(cfg Config, warmupSec, resumeTimeoutSec int) error {
	logStep(fmt.Sprintf("被动监听串口 %s，等待通讯恢复（最多 %ds）...", cfg.SerialPort, resumeTimeoutSec))
	psScript := fmt.Sprintf(`
Add-Type -AssemblyName System.IO.Ports
$p = New-Object System.IO.Ports.SerialPort '%s', %d, None, 8, One
$p.ReadTimeout = 3000
$p.WriteTimeout = 3000
$warmupSec = %d
$resumeTimeoutSec = %d
try {
  $p.Open()
  $resumeAt = $null
  $resumeDeadline = (Get-Date).AddSeconds($resumeTimeoutSec)
  while (-not $resumeAt -and (Get-Date) -lt $resumeDeadline) {
    if ($p.BytesToRead -gt 0) {
      $null = $p.ReadExisting()
      $resumeAt = Get-Date
      Write-Output 'RESUMED'
    }
    Start-Sleep -Milliseconds 200
  }
  if (-not $resumeAt) {
    Write-Output 'NO_DATA'
    $resumeAt = Get-Date
  }
  $warmupEnd = $resumeAt.AddSeconds($warmupSec)
  while ((Get-Date) -lt $warmupEnd) {
    if ($p.BytesToRead -gt 0) { $null = $p.ReadExisting() }
    Start-Sleep -Milliseconds 200
  }
  if ($p.BytesToRead -gt 0) { $null = $p.ReadExisting() }
} finally {
  if ($p.IsOpen) { $p.Close() }
  $p.Dispose()
}
`, escapePsSingle(cfg.SerialPort), cfg.BaudRate, warmupSec, resumeTimeoutSec)

	cmd := exec.Command("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", psScript)
	out, err := cmd.CombinedOutput()
	text := strings.TrimSpace(string(out))
	if err != nil {
		return fmt.Errorf("%w: %s", err, text)
	}
	if strings.Contains(text, "RESUMED") {
		logStep(fmt.Sprintf("串口 %s 通讯已恢复", cfg.SerialPort))
		logStep(fmt.Sprintf("通讯恢复后等待 %ds 再执行后续操作...", warmupSec))
	} else if strings.Contains(text, "NO_DATA") {
		logStep(fmt.Sprintf("未在 %ds 内检测到串口数据，仍等待 %ds 后继续...", resumeTimeoutSec, warmupSec))
	}
	return nil
}
