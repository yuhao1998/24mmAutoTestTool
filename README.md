# 24mmAutoTestTool

丰田 24mm（T2）自动测试 / SOC 一键升级工具。

## 仓库内容

| 目录 | 说明 |
|------|------|
| [`scripts/`](./scripts/) | SOC 升级工具源码、构建脚本、HAL 自检脚本池、ATF 入口 |

## 快速开始（Windows）

### 方式 A：使用已构建产物（推荐）

1. 进入 `scripts/SocOtaUpgrade/`
2. 双击 `SocOtaUpgrade.exe`（内置 adb，无需单独装 Android SDK）
3. 首次运行会读取同目录 `soc_ota_config.json`，可用 `-edit-config` 改串口等

```bat
SocOtaUpgrade.exe -package "D:\ota\update.zip"
```

目标机要求：Windows 10/11，系统自带 .NET Framework 4.x。

### 方式 B：从源码重新编译

```powershell
cd scripts\soc_ota_tool
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物目录：`scripts/SocOtaUpgrade/`（exe + adb tools + 配置）。

详细说明见 [`scripts/README.md`](./scripts/README.md)。

## HAL 自检脚本池

`scripts/hal_selfcheck/<module_id>/` 为各 HAL 模块 L1/L2 自检脚本，由 GUI「执行检查」或升级成功后的脚本池自动推送到车机执行。

见 [`scripts/hal_selfcheck/README.md`](./scripts/hal_selfcheck/README.md)。

## 配置注意

- `scripts/t2_atf_config.json`：ATF / 邮箱监听等配置。**请勿提交真实密码**；可参考 `t2_atf_config.json.example` 填写本地私有配置。
- `scripts/soc_ota_config.json`：串口、波特率等本机参数，可按工位修改。

## 许可 / 用途

内部测试工具，仅供项目联调与产线自检使用。
