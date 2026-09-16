package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

const configFileName = "soc_ota_config.json"

type Config struct {
	SerialPort       string `json:"serial_port"`
	BaudRate         int    `json:"baud_rate"`
	UsbModeCmd       string `json:"usb_mode_cmd"`
	RemoteOtaDir     string `json:"remote_ota_dir"`
	BootTimeoutSec   int    `json:"boot_timeout_sec"`
	UpdateTimeoutSec int    `json:"update_timeout_sec"`
	LogDir           string `json:"log_dir"`
}

func DefaultConfig() Config {
	return Config{
		SerialPort:       "COM4",
		BaudRate:         115200,
		UsbModeCmd:       "echo peripheral > /sys/bus/platform/devices/a600000.ssusb/mode",
		RemoteOtaDir:     "/data/ota",
		BootTimeoutSec:   600,
		UpdateTimeoutSec: 3600,
		LogDir:           "",
	}
}

func configPath(baseDir string) string {
	return filepath.Join(baseDir, configFileName)
}

func LoadConfig(baseDir string, overrides *Config) (Config, error) {
	cfg := DefaultConfig()
	path := configPath(baseDir)
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			if writeErr := SaveConfig(baseDir, cfg); writeErr != nil {
				return cfg, fmt.Errorf("创建默认配置失败: %w", writeErr)
			}
			fmt.Printf("已生成默认配置: %s\n", path)
		} else {
			return cfg, err
		}
	} else if err := json.Unmarshal(data, &cfg); err != nil {
		return cfg, fmt.Errorf("解析配置失败: %w", err)
	}
	applyDefaults(&cfg)
	if overrides != nil {
		mergeConfig(&cfg, overrides)
	}
	return cfg, nil
}

func SaveConfig(baseDir string, cfg Config) error {
	applyDefaults(&cfg)
	data, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(configPath(baseDir), data, 0644)
}

func applyDefaults(cfg *Config) {
	def := DefaultConfig()
	if cfg.SerialPort == "" {
		cfg.SerialPort = def.SerialPort
	}
	if cfg.BaudRate <= 0 {
		cfg.BaudRate = def.BaudRate
	}
	if cfg.UsbModeCmd == "" {
		cfg.UsbModeCmd = def.UsbModeCmd
	}
	if cfg.RemoteOtaDir == "" {
		cfg.RemoteOtaDir = def.RemoteOtaDir
	}
	if cfg.BootTimeoutSec <= 0 {
		cfg.BootTimeoutSec = def.BootTimeoutSec
	}
	if cfg.UpdateTimeoutSec <= 0 {
		cfg.UpdateTimeoutSec = def.UpdateTimeoutSec
	}
}

func mergeConfig(base *Config, over *Config) {
	if over.SerialPort != "" {
		base.SerialPort = over.SerialPort
	}
	if over.BaudRate > 0 {
		base.BaudRate = over.BaudRate
	}
	if over.UsbModeCmd != "" {
		base.UsbModeCmd = over.UsbModeCmd
	}
	if over.RemoteOtaDir != "" {
		base.RemoteOtaDir = over.RemoteOtaDir
	}
	if over.BootTimeoutSec > 0 {
		base.BootTimeoutSec = over.BootTimeoutSec
	}
	if over.UpdateTimeoutSec > 0 {
		base.UpdateTimeoutSec = over.UpdateTimeoutSec
	}
	if over.LogDir != "" {
		base.LogDir = over.LogDir
	}
}
