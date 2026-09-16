package main

import (
	"bufio"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

func main() {
	packagePath := flag.String("package", "", "OTA 升级包路径（.zip 或目录）")
	configPath := flag.String("config", "", "配置文件路径（默认与 exe 同目录 soc_ota_config.json）")
	serialPort := flag.String("com", "", "串口号，覆盖配置")
	baudRate := flag.Int("baud", 0, "波特率，覆盖配置")
	showConfig := flag.Bool("show-config", false, "显示当前配置并退出")
	editConfig := flag.Bool("edit-config", false, "交互式编辑配置并退出")
	flag.Parse()

	exePath, err := os.Executable()
	if err != nil {
		fmt.Fprintf(os.Stderr, "无法定位程序目录: %v\n", err)
		os.Exit(1)
	}
	baseDir := filepath.Dir(exePath)

	cfgBaseDir := baseDir
	if strings.TrimSpace(*configPath) != "" {
		cfgBaseDir = filepath.Dir(*configPath)
	}

	overrides := &Config{}
	if *serialPort != "" {
		overrides.SerialPort = *serialPort
	}
	if *baudRate > 0 {
		overrides.BaudRate = *baudRate
	}

	cfg, err := LoadConfig(cfgBaseDir, overrides)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[ERROR] %v\n", err)
		os.Exit(1)
	}

	if *showConfig {
		fmt.Printf("配置文件: %s\n", configPath(baseDir))
		fmt.Printf("  serial_port       = %s\n", cfg.SerialPort)
		fmt.Printf("  baud_rate         = %d\n", cfg.BaudRate)
		fmt.Printf("  usb_mode_cmd      = %s\n", cfg.UsbModeCmd)
		fmt.Printf("  remote_ota_dir    = %s\n", cfg.RemoteOtaDir)
		fmt.Printf("  boot_timeout_sec  = %d\n", cfg.BootTimeoutSec)
		fmt.Printf("  update_timeout_sec= %d\n", cfg.UpdateTimeoutSec)
		fmt.Printf("  log_dir           = %s\n", cfg.LogDir)
		os.Exit(0)
	}

	if *editConfig {
		if err := interactiveEditConfig(baseDir, &cfg); err != nil {
			fmt.Fprintf(os.Stderr, "[ERROR] %v\n", err)
			os.Exit(1)
		}
		fmt.Println("配置已保存:", configPath(baseDir))
		os.Exit(0)
	}

	fmt.Println("丰田T2版本测试工具")
	fmt.Println("配置文件:", configPath(baseDir))
	fmt.Println("修改串口/波特率: SocOtaUpgrade.exe -edit-config  或编辑 soc_ota_config.json")
	fmt.Println()

	code := runUpgrade(cfg, *packagePath)
	if code != 0 {
		fmt.Println()
		fmt.Println("按 Enter 退出...")
		_, _ = bufio.NewReader(os.Stdin).ReadBytes('\n')
	}
	os.Exit(code)
}

func interactiveEditConfig(baseDir string, cfg *Config) error {
	reader := bufio.NewReader(os.Stdin)
	fmt.Println("=== 编辑配置（直接回车保留当前值）===")
	cfg.SerialPort = promptString(reader, "串口号 COM", cfg.SerialPort)
	cfg.BaudRate = promptInt(reader, "波特率", cfg.BaudRate)
	cfg.UsbModeCmd = promptString(reader, "USB 切 dev 命令", cfg.UsbModeCmd)
	cfg.RemoteOtaDir = promptString(reader, "车机 OTA 目录", cfg.RemoteOtaDir)
	cfg.BootTimeoutSec = promptInt(reader, "启动超时(秒)", cfg.BootTimeoutSec)
	cfg.UpdateTimeoutSec = promptInt(reader, "升级超时(秒)", cfg.UpdateTimeoutSec)
	cfg.LogDir = promptString(reader, "本地日志目录(空=exe/logs)", cfg.LogDir)
	return SaveConfig(baseDir, *cfg)
}

func promptString(r *bufio.Reader, label, current string) string {
	fmt.Printf("%s [%s]: ", label, current)
	line, _ := r.ReadString('\n')
	line = strings.TrimSpace(line)
	if line == "" {
		return current
	}
	return line
}

func promptInt(r *bufio.Reader, label string, current int) int {
	fmt.Printf("%s [%d]: ", label, current)
	line, _ := r.ReadString('\n')
	line = strings.TrimSpace(line)
	if line == "" {
		return current
	}
	v, err := strconv.Atoi(line)
	if err != nil {
		return current
	}
	return v
}
