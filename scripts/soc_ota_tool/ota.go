package main

import (
	"archive/zip"
	"bufio"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

type packageInfo struct {
	isZip      bool
	sourcePath string
}

func logStep(msg string) {
	fmt.Printf("[%s] %s\n", time.Now().Format("2006-01-02 15:04:05"), msg)
}

func logErr(msg string) {
	fmt.Printf("[ERROR] %s\n", msg)
}

func waitAdbDevice(runner AdbRunner, timeoutSec int) bool {
	logStep(fmt.Sprintf("等待 adb 设备就绪（最多 %ds）...", timeoutSec))
	deadline := time.Now().Add(time.Duration(timeoutSec) * time.Second)
	for time.Now().Before(deadline) {
		if adbDeviceReady(runner) {
			logStep("adb 设备已连接")
			return true
		}
		time.Sleep(2 * time.Second)
	}
	return false
}

func waitAdbDisconnect(runner AdbRunner, timeoutSec int) {
	logStep("以 adb 断开作为车机开始 reboot 的标识，被动等待...")
	deadline := time.Now().Add(time.Duration(timeoutSec) * time.Second)
	for time.Now().Before(deadline) {
		if !adbDeviceReady(runner) {
			logStep("检测到 adb 已断开（reboot 已开始）")
			return
		}
		time.Sleep(2 * time.Second)
	}
	logStep("未检测到 adb 断开（可能 reboot 极快），继续后续流程...")
}

func waitAfterOtaReboot(runner AdbRunner, cfg Config, bootTimeoutSec int) error {
	waitAdbDisconnect(runner, 90)
	logStep(fmt.Sprintf(
		"reboot 已开始，被动监听串口 %s 直至通讯恢复，恢复后再等待 150s ...",
		cfg.SerialPort))
	if err := waitSerialResumeThenWarmup(cfg, 150, bootTimeoutSec); err != nil {
		logStep("串口被动监听: " + err.Error())
	}
	logStep("150s 被动等待完成，开始经串口切换 dev 并连接 adb 进行槽位/版本校验 ...")
	if !adbDeviceReady(runner) {
		logStep("adb 未连接，经串口 su → start adbd → 切换 USB peripheral（dev）模式 ...")
		if err := switchToDevModeViaSerial(cfg, runner); err != nil {
			return err
		}
	}
	if out, _, _ := runner.Run([]string{"shell", "getprop", "ro.boot.slot_suffix"}, true); strings.TrimSpace(out) != "" {
		logStep("========== 升级后 A/B 槽位检查 ==========")
		logStep("  adb shell getprop ro.boot.slot_suffix = " + strings.TrimSpace(out))
		logStep("==========================================")
	}
	return waitBootCompleted(runner, bootTimeoutSec)
}

func waitBootCompleted(runner AdbRunner, timeoutSec int) error {
	logStep("等待系统启动完成（sys.boot_completed=1）...")
	_, _, _ = runner.Run([]string{"wait-for-device"}, true)
	deadline := time.Now().Add(time.Duration(timeoutSec) * time.Second)
	for time.Now().Before(deadline) {
		out, _, _ := runner.Run([]string{"shell", "getprop", "sys.boot_completed"}, true)
		if strings.TrimSpace(out) == "1" {
			time.Sleep(5 * time.Second)
			logStep("系统 boot 完成")
			return nil
		}
		time.Sleep(3 * time.Second)
	}
	return fmt.Errorf("等待 boot 完成超时（%ds）", timeoutSec)
}

func enableAdbRoot(runner AdbRunner) error {
	logStep("执行 adb root ...")
	_, _, _ = runner.Run([]string{"root"}, true)
	time.Sleep(3 * time.Second)
	_, _, _ = runner.Run([]string{"wait-for-device"}, true)
	out, _, _ := runner.Run([]string{"shell", "id"}, true)
	if strings.Contains(out, "uid=0") {
		logStep("adb root 成功")
		return nil
	}
	return fmt.Errorf("adb root 失败，请确认车机处于 dev 模式且允许 root")
}

func validateOtaPackage(path string) (packageInfo, error) {
	st, err := os.Stat(path)
	if err != nil {
		return packageInfo{}, fmt.Errorf("升级包不存在: %s", path)
	}
	if st.IsDir() {
		payload := filepath.Join(path, "payload.bin")
		props := filepath.Join(path, "payload_properties.txt")
		if _, err := os.Stat(payload); err != nil {
			return packageInfo{}, fmt.Errorf("目录内缺少 payload.bin: %s", path)
		}
		if _, err := os.Stat(props); err != nil {
			return packageInfo{}, fmt.Errorf("目录内缺少 payload_properties.txt: %s", path)
		}
		return packageInfo{isZip: false, sourcePath: path}, nil
	}
	if !strings.EqualFold(filepath.Ext(path), ".zip") {
		return packageInfo{}, fmt.Errorf("升级包须为 .zip 或含 payload 的目录: %s", path)
	}
	zr, err := zip.OpenReader(path)
	if err != nil {
		return packageInfo{}, err
	}
	defer zr.Close()
	hasPayload, hasProps := false, false
	for _, f := range zr.File {
		base := filepath.Base(f.Name)
		if base == "payload.bin" {
			hasPayload = true
		}
		if base == "payload_properties.txt" {
			hasProps = true
		}
	}
	if !hasPayload {
		return packageInfo{}, fmt.Errorf("zip 内缺少 payload.bin: %s", path)
	}
	if !hasProps {
		return packageInfo{}, fmt.Errorf("zip 内缺少 payload_properties.txt: %s", path)
	}
	return packageInfo{isZip: true, sourcePath: path}, nil
}

func prepareLocalUpdateZip(info packageInfo, workDir string) (string, error) {
	updateZip := filepath.Join(workDir, "update.zip")
	_ = os.Remove(updateZip)
	if info.isZip {
		src, err := os.Open(info.sourcePath)
		if err != nil {
			return "", err
		}
		defer src.Close()
		dst, err := os.Create(updateZip)
		if err != nil {
			return "", err
		}
		_, err = io.Copy(dst, src)
		dst.Close()
		if err != nil {
			return "", err
		}
	} else {
		if err := zipDirectory(info.sourcePath, updateZip); err != nil {
			return "", err
		}
	}
	logStep("本地 update.zip 已准备: " + updateZip)
	return updateZip, nil
}

func zipDirectory(srcDir, destZip string) error {
	out, err := os.Create(destZip)
	if err != nil {
		return err
	}
	defer out.Close()
	w := zip.NewWriter(out)
	defer w.Close()
	return filepath.Walk(srcDir, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return err
		}
		rel, err := filepath.Rel(srcDir, path)
		if err != nil {
			return err
		}
		rel = filepath.ToSlash(rel)
		fw, err := w.Create(rel)
		if err != nil {
			return err
		}
		f, err := os.Open(path)
		if err != nil {
			return err
		}
		defer f.Close()
		_, err = io.Copy(fw, f)
		return err
	})
}

func isASCIIPath(path string) bool {
	for _, r := range path {
		if r > 127 {
			return false
		}
	}
	return true
}

func needsAsciiPushStaging(localZip, adbPath string) bool {
	if !isASCIIPath(localZip) {
		return true
	}
	return adbPath != "" && !isASCIIPath(adbPath)
}

func resolvePushSource(localZip, adbPath string) (string, error) {
	tempDir := filepath.Join(os.TempDir(), "soc_ota_push")
	tempZip := filepath.Join(tempDir, "update.zip")
	if filepath.Clean(localZip) == filepath.Clean(tempZip) {
		return localZip, nil
	}
	if !needsAsciiPushStaging(localZip, adbPath) {
		return localZip, nil
	}
	if err := os.MkdirAll(tempDir, 0755); err != nil {
		return "", err
	}
	src, err := os.Open(localZip)
	if err != nil {
		return "", err
	}
	defer src.Close()
	dst, err := os.Create(tempZip)
	if err != nil {
		return "", err
	}
	if _, err := io.Copy(dst, src); err != nil {
		dst.Close()
		return "", err
	}
	dst.Close()
	logStep("adb push 使用 ASCII 临时路径（避免中文/空格路径导致推送异常）: " + tempZip)
	return tempZip, nil
}

func pushAndExtractOta(runner AdbRunner, cfg Config, localZip string) error {
	dir := strings.TrimRight(cfg.RemoteOtaDir, "/")

	pushSource, err := resolvePushSource(localZip, adbPath)
	if err != nil {
		return err
	}

	logStep("推送 update.zip 到 " + dir + "/ ...")
	_, _, _ = runner.Run([]string{"shell", "mkdir -p " + dir}, true)
	_, _, err = runner.Run([]string{"push", pushSource, dir + "/"}, false)
	if err != nil {
		return err
	}

	listing, _, _ := runner.Run([]string{"shell", "ls -1 " + dir + " 2>/dev/null"}, true)
	if strings.Contains(listing, "upda") && !strings.Contains(listing, "update.zip") {
		return fmt.Errorf("车机端推送文件名异常（发现 upda 而非 update.zip），请将工具部署到纯 ASCII 路径")
	}

	logStep("关闭 SELinux 强制模式 (setenforce 0) ...")
	_, _, _ = runner.Run([]string{"shell", "setenforce 0"}, true)
	enforce, _, _ := runner.Run([]string{"shell", "getenforce"}, true)
	logStep("当前 SELinux 状态: " + strings.TrimSpace(enforce))

	logStep("解压 update.zip ...")
	unzipCmd := fmt.Sprintf("cd %s && (unzip -o update.zip || busybox unzip -o update.zip)", dir)
	_, _, _ = runner.Run([]string{"shell", unzipCmd}, true)

	check, _, _ := runner.Run([]string{"shell", "ls " + dir + "/payload.bin " + dir + "/payload_properties.txt 2>/dev/null"}, true)
	if !strings.Contains(check, "payload.bin") || !strings.Contains(check, "payload_properties.txt") {
		return fmt.Errorf("解压后未在 %s 找到 payload.bin / payload_properties.txt", dir)
	}
	logStep("OTA 包已就绪于 " + dir)
	return nil
}

func startSocUpdate(runner AdbRunner, cfg Config, clientLog string) (*exec.Cmd, error) {
	dir := strings.TrimRight(cfg.RemoteOtaDir, "/")
	updateCmd := fmt.Sprintf(
		`cd %s && PROP=$(cat payload_properties.txt) && update_engine_client --update --payload=file://%s/payload.bin --headers="$PROP"`,
		dir, dir,
	)
	logStep("启动 update_engine_client（adb shell 内执行，Linux 换行/字符）...")
	return startBackgroundAdb([]string{"shell", updateCmd}, clientLog, clientLog+".err")
}

var (
	successPatterns = []string{
		"UPDATED_NEED_REBOOT",
		"Update successfully applied",
		"payload_application_complete.*error_code=0",
		"onPayloadApplicationComplete.*0",
	}
	failPatterns = []*regexp.Regexp{
		regexp.MustCompile(`UPDATE_STATUS_REPORTING_ERROR`),
		regexp.MustCompile(`Update failed`),
		regexp.MustCompile(`ErrorCode::kDownload`),
		regexp.MustCompile(`ErrorCode::kPayload`),
		regexp.MustCompile(`ErrorCode::kInstall`),
		regexp.MustCompile(`ErrorCode::kMetadata`),
		regexp.MustCompile(`ErrorCode::kSignature`),
		regexp.MustCompile(`ErrorCode::kVerity`),
		regexp.MustCompile(`ErrorCode::kTransfer`),
	}
)

func waitUpdateComplete(updateProc *exec.Cmd, engineLog string, timeoutSec int) error {
	logStep(fmt.Sprintf("监听升级进度（超时 %ds）...", timeoutSec))
	deadline := time.Now().Add(time.Duration(timeoutSec) * time.Second)
	exitCh := make(chan error, 1)
	go func() { exitCh <- updateProc.Wait() }()

	for time.Now().Before(deadline) {
		if data, err := tailFile(engineLog, 80); err == nil {
			for _, p := range failPatterns {
				if p.MatchString(data) {
					return fmt.Errorf("升级失败，详见日志: %s", engineLog)
				}
			}
			for _, p := range successPatterns {
				matched, _ := regexp.MatchString(p, data)
				if matched {
					logStep("检测到安装完成信号，随后 PC 侧将执行 adb reboot 激活升级 ...")
					return nil
				}
			}
		}
		select {
		case err := <-exitCh:
			if err != nil {
				return fmt.Errorf("update_engine_client 异常: %w", err)
			}
			if updateProc.ProcessState != nil && updateProc.ProcessState.ExitCode() != 0 {
				return fmt.Errorf("update_engine_client 异常退出 (exit=%d)", updateProc.ProcessState.ExitCode())
			}
			time.Sleep(3 * time.Second)
			if data, err := os.ReadFile(engineLog); err == nil {
				text := string(data)
				for _, p := range successPatterns {
					matched, _ := regexp.MatchString(p, text)
					if matched {
						return nil
					}
				}
			}
			logStep("update_engine_client 已结束，视为升级完成")
			return nil
		case <-time.After(5 * time.Second):
		}
	}
	return fmt.Errorf("升级超时（%ds），详见: %s", timeoutSec, engineLog)
}

func tailFile(path string, lines int) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	var ring []string
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		ring = append(ring, sc.Text())
		if len(ring) > lines {
			ring = ring[1:]
		}
	}
	return strings.Join(ring, "\n"), sc.Err()
}

func printDeviceVersion(runner AdbRunner) {
	props := []string{
		"ro.build.display.id",
		"ro.build.version.incremental",
		"ro.build.fingerprint",
		"ro.vendor.build.version",
		"ro.product.build.version",
		"ro.build.description",
	}
	logStep("========== 车机版本信息 ==========")
	for _, p := range props {
		out, _, _ := runner.Run([]string{"shell", "getprop", p}, true)
		out = strings.TrimSpace(out)
		if out != "" {
			fmt.Printf("  %s = %s\n", p, out)
		}
	}
	logStep("==================================")
}

func readPackagePathInteractive(flagPath string) (string, error) {
	if strings.TrimSpace(flagPath) != "" {
		p, err := filepath.Abs(flagPath)
		if err != nil {
			return "", err
		}
		return p, nil
	}
	fmt.Print("请输入 OTA 升级包路径（.zip 或目录）: ")
	reader := bufio.NewReader(os.Stdin)
	line, err := reader.ReadString('\n')
	if err != nil {
		return "", err
	}
	line = strings.Trim(strings.TrimSpace(line), `"`)
	if line == "" {
		return "", fmt.Errorf("升级包路径不能为空")
	}
	return filepath.Abs(line)
}

func runUpgrade(cfg Config, packagePath string) int {
	baseDir, _ := os.Executable()
	baseDir = filepath.Dir(baseDir)
	logRoot := cfg.LogDir
	if logRoot == "" {
		logRoot = filepath.Join(baseDir, "logs")
	}
	sessionDir := filepath.Join(logRoot, time.Now().Format("20060102_150405"))
	workDir := filepath.Join(sessionDir, "work")
	_ = os.MkdirAll(workDir, 0755)
	engineLog := filepath.Join(sessionDir, "update_engine.log")
	clientLog := filepath.Join(sessionDir, "update_engine_client.log")

	var logcatCmd, updateCmd *exec.Cmd
	defer func() {
		if logcatCmd != nil && logcatCmd.Process != nil {
			_ = logcatCmd.Process.Kill()
		}
		if updateCmd != nil && updateCmd.Process != nil {
			_ = updateCmd.Process.Kill()
		}
	}()

	runner := AdbRunner{}
	logStep("=== 丰田T2版本测试工具 开始 ===")
	logStep("配置: 串口=" + cfg.SerialPort + " 波特率=" + fmt.Sprint(cfg.BaudRate))
	logStep("日志目录: " + sessionDir)

	if err := initEmbeddedAdb(); err != nil {
		logErr(err.Error())
		return 1
	}
	logStep("内置 adb: " + adbPath)

	resolved, err := readPackagePathInteractive(packagePath)
	if err != nil {
		logErr(err.Error())
		return 1
	}
	info, err := validateOtaPackage(resolved)
	if err != nil {
		logErr(err.Error())
		return 1
	}
	logStep("升级包校验通过: " + resolved)

	if adbDeviceReady(runner) {
		logStep("检测到 adb 已连接（dev 模式）")
	} else {
		logStep("adb 未连接，经串口 su → start adbd → 切换 USB peripheral（dev）模式 ...")
		if err := switchToDevModeViaSerial(cfg, runner); err != nil {
			logErr(err.Error())
			return 1
		}
	}
	if err := enableAdbRoot(runner); err != nil {
		logErr(err.Error())
		return 1
	}
	localZip, err := prepareLocalUpdateZip(info, workDir)
	if err != nil {
		logErr(err.Error())
		return 1
	}
	if err := pushAndExtractOta(runner, cfg, localZip); err != nil {
		logErr(err.Error())
		return 1
	}

	logStep("开始采集 update_engine 日志 -> " + engineLog)
	logcatCmd, err = startBackgroundAdb(
		[]string{"logcat", "-v", "time", "-s", "update_engine:*", "UpdateEngine:*", "update_engine_client:*"},
		engineLog, engineLog+".err",
	)
	if err != nil {
		logErr(err.Error())
		return 1
	}
	time.Sleep(2 * time.Second)

	updateCmd, err = startSocUpdate(runner, cfg, clientLog)
	if err != nil {
		logErr(err.Error())
		return 1
	}
	if err := waitUpdateComplete(updateCmd, engineLog, cfg.UpdateTimeoutSec); err != nil {
		logErr(err.Error())
		logStep("完整日志目录: " + sessionDir)
		return 1
	}

	logStep("=== OTA 安装完成（update_engine 已成功应用）===")
	logStep("OTA 安装已完成，PC 侧执行 adb reboot 以激活 A/B 槽位切换 ...")
	logStep("说明: reboot 后 PC 侧仅被动监听 adb/串口，不 kill-server、不提前切 dev")
	_, _, _ = runner.Run([]string{"reboot"}, true)
	logStep("等待车机 reboot 并完成启动 ...")
	if err := waitAfterOtaReboot(runner, cfg, cfg.BootTimeoutSec); err != nil {
		logErr(err.Error())
		return 1
	}
	printDeviceVersion(runner)
	logStep("=== 丰田T2版本测试工具 完成 ===")
	return 0
}
