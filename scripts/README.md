# SOC 一键升级工具（scripts）

本目录是 [24mmAutoTestTool](https://github.com/yuhao1998/24mmAutoTestTool) 的核心：SOC OTA 升级 GUI/CLI、HAL 自检脚本池、ATF 脚本入口。

## 目录结构

| 路径 | 说明 |
|------|------|
| `SocOtaUpgrade/` | **可直接分发**的 Windows 工具包（exe + 内置 adb + 配置） |
| `soc_ota_tool/` | C# 源码与 `build.ps1` 构建脚本 |
| `hal_selfcheck/` | 各 HAL 模块 L1/L2 自检脚本 |
| `t2_upgrade_entry.py` | ATF / 邮箱 / TCP 监听升级入口 |
| `t2_script_pool.json` | 脚本池注册表（路径指向 `hal_selfcheck/`） |
| `t2_atf_config.json` | ATF/邮箱等配置（勿填真实密码进仓库） |

## 两种使用方式

| 方式 | 文件 | 说明 |
|------|------|------|
| 脚本版 | `soc_ota_upgrade.bat` + `.ps1` | 需系统 PATH 中有 adb |
| **推荐** | `SocOtaUpgrade/SocOtaUpgrade.exe` | **内置 adb**，内置串口，适合产线/测试机 |

---

## 构建 SocOtaUpgrade.exe

**环境**：Windows + .NET Framework 4.x（自带 `csc.exe`）+ 网络（首次下载 platform-tools）

```powershell
cd scripts\soc_ota_tool
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物：`scripts/SocOtaUpgrade/`（整夹拷贝到目标机即可，不要只拷 exe）。

---

## 配置串口 / 波特率

首次运行会在 **exe 同目录** 生成 `soc_ota_config.json`。

**方式 1：交互编辑**

```bat
SocOtaUpgrade.exe -edit-config
```

**方式 2：直接改 JSON**（参考 `soc_ota_config.json.example`）

**方式 3：命令行覆盖**

```bat
SocOtaUpgrade.exe -com COM5 -baud 921600 -package "D:\ota\update.zip"
```

---

## 运行升级

```bat
SocOtaUpgrade.exe
SocOtaUpgrade.exe -package "D:\ota\update.zip"
```

日志：`<exe目录>\logs\<时间戳>\`

---

## T2 ATF 升级入口（脚本驱动）

`t2_upgrade_entry.py` 在 `SocOtaUpgrade` 之上封装一层「脚本驱动入口」，支持两种触发方式，便于接入 ATF/CI 流程。**不修改 `BMC_ATF_auto-exec-controller` 任何代码**，OSS/ATF 配置只读复用其 `config.json`。

### 依赖

- Python 3 + `requests`（`--listen` 模式还需 `oss2`）
- 已构建 `SocOtaUpgrade/SocOtaUpgrade.exe`（见上文「构建」）

### 两种使用方式

| 方式 | 命令 | 说明 |
|------|------|------|
| 直传升级包 | `python t2_upgrade_entry.py --package "D:\ota\update.zip"` | 跳过 TCP/OSS，直接升级本地包 |
| 监听触发 | `python t2_upgrade_entry.py --listen`（无参默认即监听） | 监听 TCP；若 `email.enabled=true` 则同时轮询邮箱 |
| 仅邮箱 | `python t2_upgrade_entry.py --listen-email` | 仅 IMAP 轮询邮件触发 |

启动器：`run_t2_upgrade.cmd`（双击或命令行带参）。

### 配置

`t2_atf_config.json`：

| 字段 | 说明 |
|------|------|
| `soc_ota_exe` | `SocOtaUpgrade.exe` 相对路径 |
| `bmc_config_path` | 只读复用 BMC `config.json` 的 oss/atf/pc 配置 |
| `tcp.port` | 监听端口（默认 8007，避免与 BMC `build_notification` 的 8006 冲突）|
| `email.enabled` | 是否启用 IMAP 邮箱监听（默认 false）|
| `email.imap_host/port/username/password` | 邮箱服务器与账号 |
| `email.subject_keyword` | 主题过滤关键字（默认 `T2_OTA`）|
| `email.package_save_dir` | 邮件取到的升级包本地保存目录（GUI「升级包保存」）|
| `atf_upload.enabled` | ATF 上传开关 |

### 邮箱触发（IMAP）

在 `t2_atf_config.json` 填好邮箱后设 `email.enabled=true`，监听模式会并行轮询未读邮件。

GUI（SocOtaUpgrade）可配置并同步：收件箱 / 发件人 / **升级包保存路径**。

邮件主题建议包含 `T2_OTA`，正文给出升级包地址（本地 / UNC / HTTP）：

```text
{"package_url":"http://192.168.1.10/ota/update.zip"}
```

```text
{"package_path":"\\\\192.168.1.10\\share\\full_update.zip"}
```

```text
package_path=E:\BMCSJB\update.zip
```

流程：**解析地址 → 下载/复制到「升级包保存」目录 → 用本地路径执行升级**。  
已处理 UID 记在 `t2_email_processed_uids.json`，避免重复触发。

### build_pigeon 联动

编译/CI 服务器上 `build_pigeon.py` 的 `--ip` 指向运行本入口的 PC，`--port` 指向 `t2_atf_config.json` 的 `tcp.port`：

```bash
python3 build_pigeon.py --ip <PC_IP> --port 8007 \
  --soc_version 5.17.51.R40... --parent_dir 2000-01-01-00-00-00-CI-00
```

### 本地模拟联动（BMC 通知 scripts，跳过 OSS）

`test_trigger_local.py` 模拟 BMC 侧 `build_pigeon` 的 TCP 通知，但发的是**本地升级包路径**，跳过 OSS 下载，便于本地联调：

```bat
:: 1) PC 侧先启动监听
python t2_upgrade_entry.py --listen --port 8007

:: 2) 另开终端，模拟 BMC 通知 scripts 升级
python test_trigger_local.py --ip 127.0.0.1 --port 8007 --package "e:\BMCSJB\update.zip" --soc_version LOCAL-TEST-v1 --parent_dir local-sim-001
```

监听端收到 `{"package_path": "..."}` 后：ACK → 直接用本地包调 `SocOtaUpgrade` 升级 → 落地 `progress.json` / `version_verify.json` / `hal_status.json` / `t2_task_summary.json`。

### 触发消息协议

监听端兼容两种消息格式：

| 字段 | 含义 | 走哪条路 |
|------|------|----------|
| `package_path` | 本地升级包路径 | 直接升级（跳过 OSS）|
| `soc_version` + `parent_dir_name` | OSS 路径组件 | OSS 下载后升级 |
| 两者都有 | — | 优先用 `package_path`（本地模拟覆盖 OSS）|

### 监听触发 vs 手动触发 的行为差异

| 维度 | 监听触发（build_pigeon / test_trigger_local）| 手动 `--package` |
|------|----------------------------------------------|------------------|
| GUI 显示 | ✅ `-gui -report` | ✅ `-gui` |
| 升级+验证完成后 | **弹窗说明再退出**（不直接关） | 自动关闭 |
| 上报 ATF | ✅ 上传 version_verify/hal_status/summary + 回报状态 | ❌ 不上报 |
| ATF 上传开关 | `atf_upload.enabled=true`（默认开）| — |

仅监听触发才上报 ATF；手动 `--package` 只本地跑升级+落地 JSON，不触碰 ATF。

### 实时阶段进度（progress.json）

升级期间 `SocOtaUpgrade` 在 session 目录写 `progress.json`，并在 `session.log` 输出 `[STAGE]` 标记行，便于脚本轮询：

```json
{"stage":"update_engine","status":"running","message":"update_engine 应用 OTA 中","timestamp":"2026-07-04 14:44:23","elapsed_sec":55}
```

阶段 ID 顺序：`init → package_check → dev_mode → pre_slot → push → update_engine → reboot → boot_wait → post_logs → version_verify → hal_check → done`（失败为 `failed`，终止为 `cancelled`）。

`t2_upgrade_entry.py` 在 `run_upgrade()` 中已自动轮询 `progress.json` 并打印 `[STAGE]` 阶段切换。

### 产物

- 升级日志：`SocOtaUpgrade/logs/<时间戳>/`（`session.log`、`version_verify.json`、`hal_status.json`、`post_reboot/` 等）
- 任务摘要：同目录 `t2_task_summary.json`（含退出码、版本/HAL 校验结果）
- 下载的升级包：`t2_downloads/`（`keep_downloaded_package=true` 时保留）

### 脚本触发时的进度与 UI 显示

`SocOtaUpgrade.exe` 由 `Program.cs` 根据参数自动选择运行模式：

| 启动方式 | 模式 | 显示 |
|----------|------|------|
| 无参数 / 双击 | GUI | `MainForm` 图形界面（手动选包、按钮操作）|
| `-gui -package <path>` | **GUI 自动启动** | 图形界面 + 自动填包 + 自动开始升级（**脚本触发推荐**）|
| `-cli -package <path>` 等 | CLI | 控制台窗口，纯文字日志 |

`t2_atf_config.json` 默认 `soc_ota_extra_args = ["-gui"]`，脚本触发走 **GUI 自动启动** 模式：

- ✅ 弹出完整图形界面：进度条（`ProgressBar` 跑马灯）、运行日志（`RichTextBox` 黑/红色）、检查结果表（`ListView`）、状态栏
- ✅ 自动填入包路径并自动开始升级，无需人工点击
- ✅ 升级成功后自动关闭窗口，Python 拿到退出码 0 继续后续流程
- ⚠️ 失败时弹窗提示并留观（不自动关闭），用户查看错误后关闭，Python 拿到对应退出码（3=Recovery，4=版本失败，5=HAL 失败，1=其他）
- ⚠️ Python 父进程仍无法实时捕获进度文本（GUI 写入的是窗体控件）；如需在 Python 侧实时获取进度，应 tail `SocOtaUpgrade/logs/<时间戳>/session.log`
- 升级进行中可在界面点「结束升级」或另开终端执行 `SocOtaUpgrade.exe -stop` 终止

> 如需**无人值守、无窗口**模式（产线后台），把 `t2_atf_config.json` 的 `soc_ota_extra_args` 改为 `["-cli"]`，exe 只弹控制台窗口打文字日志，不显示 GUI。

### 两种触发模式对比

| 维度 | `-gui -package`（默认）| `-cli -package` |
|------|------------------------|------------------|
| 窗体 | 图形界面 | 控制台窗口 |
| 进度条 | ✅ | ❌ |
| 结果列表 | ✅ | ❌ |
| 状态栏 | ✅ | ❌ |
| 自动开始升级 | ✅ | ✅ |
| 成功后行为 | 自动关闭 | 进程退出 |
| 失败后行为 | 弹窗留观 | 进程退出 |
| 适用场景 | 想看进度/结果 | 无人值守后台 |

### 退出码

| 码 | 含义 |
|----|------|
| 0 | 升级成功 |
| 2 | 升级包不存在 |
| 3 | OSS 下载失败 |
| 130 | 用户中断 |
| 其他 | 透传 `SocOtaUpgrade.exe` 退出码（3=Recovery，4=版本校验失败，5=HAL 失败）|

### ATF 上传（预留）

`upload_to_atf()` 为预留接口，`atf_upload.enabled=false` 时仅落地产物。后续接入时置 `true` 并在该函数内实现：登录 ATF → 注册 PC → 上传 `version_verify.json` / `hal_status.json` / 汇总报告到 `/api/file`。
