package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
)

//go:embed bundled/platform-tools.zip
var platformToolsZip []byte

var (
	adbPath     string
	adbInitOnce sync.Once
	adbInitErr  error
)

func initEmbeddedAdb() error {
	adbInitOnce.Do(func() {
		if len(platformToolsZip) < 100 {
			adbInitErr = fmt.Errorf("内置 platform-tools 缺失，请使用 build.ps1 构建")
			return
		}
		sum := sha256.Sum256(platformToolsZip)
		cacheRoot := filepath.Join(os.Getenv("LOCALAPPDATA"), "SocOtaUpgrade", "platform-tools")
		marker := filepath.Join(cacheRoot, "bundle.sha256")
		adbInitErr = os.MkdirAll(cacheRoot, 0755)
		if adbInitErr != nil {
			return
		}
		want := hex.EncodeToString(sum[:])
		if b, err := os.ReadFile(marker); err == nil && strings.TrimSpace(string(b)) == want {
			adbPath = filepath.Join(cacheRoot, "adb.exe")
			if _, err := os.Stat(adbPath); err == nil {
				return
			}
		}
		if err := extractZipFromBytes(platformToolsZip, cacheRoot); err != nil {
			adbInitErr = fmt.Errorf("解压 adb 失败: %w", err)
			return
		}
		adbPath = filepath.Join(cacheRoot, "adb.exe")
		if _, err := os.Stat(adbPath); err != nil {
			adbInitErr = fmt.Errorf("adb.exe 不存在: %s", adbPath)
			return
		}
		_ = os.WriteFile(marker, []byte(want), 0644)
	})
	return adbInitErr
}

func extractZipFromBytes(data []byte, dest string) error {
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return err
	}
	for _, f := range zr.File {
		name := filepath.Join(dest, filepath.Base(f.Name))
		if f.FileInfo().IsDir() {
			if err := os.MkdirAll(name, 0755); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(name), 0755); err != nil {
			return err
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		out, err := os.OpenFile(name, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, f.Mode())
		if err != nil {
			rc.Close()
			return err
		}
		_, copyErr := io.Copy(out, rc)
		out.Close()
		rc.Close()
		if copyErr != nil {
			return copyErr
		}
	}
	return nil
}

type AdbRunner struct{}

func (AdbRunner) Run(args []string, allowFailure bool) (string, int, error) {
	if err := initEmbeddedAdb(); err != nil {
		return "", -1, err
	}
	cmd := exec.Command(adbPath, args...)
	cmd.Env = os.Environ()
	out, err := cmd.CombinedOutput()
	text := strings.TrimSpace(string(out))
	code := 0
	if cmd.ProcessState != nil {
		code = cmd.ProcessState.ExitCode()
	}
	if err != nil && allowFailure {
		return text, code, nil
	}
	if !allowFailure && code != 0 {
		return text, code, fmt.Errorf("adb %s failed (exit=%d): %s", strings.Join(args, " "), code, text)
	}
	return text, code, nil
}

func adbDeviceReady(runner AdbRunner) bool {
	out, _, _ := runner.Run([]string{"devices"}, true)
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		fields := strings.Fields(line)
		if len(fields) == 2 && fields[1] == "device" {
			return true
		}
	}
	return false
}

func startBackgroundAdb(args []string, stdoutPath, stderrPath string) (*exec.Cmd, error) {
	if err := initEmbeddedAdb(); err != nil {
		return nil, err
	}
	stdout, err := os.Create(stdoutPath)
	if err != nil {
		return nil, err
	}
	stderr, err := os.Create(stderrPath)
	if err != nil {
		stdout.Close()
		return nil, err
	}
	cmd := exec.Command(adbPath, args...)
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	if err := cmd.Start(); err != nil {
		stdout.Close()
		stderr.Close()
		return nil, err
	}
	return cmd, nil
}
