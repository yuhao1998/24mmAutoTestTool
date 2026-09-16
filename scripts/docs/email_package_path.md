# 邮箱识别与升级包下载路径

实现位置：`t2_upgrade_entry.py`（邮箱轮询 + 路径解析 + 落地保存 + OTA 包整理）。  
GUI 侧：`SocOtaUpgrade` →「升级包保存」路径，经 `EmailListenConfigHelper` 同步到 `t2_atf_config.json` 的 `email.package_save_dir`。

## 整体流程

```text
轮询邮箱(EWS/IMAP/POP3)
    → 关键字匹配（subject_keyword，主题或正文）
    → 解析升级包地址（parse_upgrade_trigger_from_text）
    → 下载/复制到 package_save_dir（fetch_package_to_local）
    → 整理为可升级 OTA（prepare_ota_package_for_upgrade，.7z 自动解压）
    → 调用 SocOtaUpgrade 完整升级
```

已处理邮件 UID 写入 `t2_email_processed_uids.json`，避免重复触发。

---

## 1. 邮件里如何写升级包路径

解析函数：`parse_upgrade_trigger_from_text()` / `_parse_trigger_from_mail()`。  
**主题 + 正文一起扫**；关键字 `subject_keyword`（默认 `T2_OTA`）出现在主题**或**正文即可。

### 优先级（命中即返回）

| 顺序 | 形式 | 示例 |
|------|------|------|
| 1 | JSON 对象 | `{"package_url":"\\\\192.168.1.10\\share\\ota.7z"}` |
| 2 | 键值对 | `package_path=D:\ota\update.zip` |
| 3 | 裸路径 / URL | 直接写 `http://...` / `\\IP\share\...` / `E:\...\xxx.zip` |

### 支持的字段名（等价）

- 包地址：`package_url` / `package_path` / `url` / `ota_url` / `download_url`
- OSS：`soc_version` + `parent_dir_name`（无本地/HTTP 地址时走 OSS 下载）

### 支持的源类型

| 类型 | 识别规则 | 落地方式 |
|------|----------|----------|
| HTTP(S) | `https?://...`（可带 `.zip/.7z/...`，或纯 IP URL） | 下载到保存目录 |
| UNC | `\\host\share\...` 或 `//host/share/...` | `copy` 到保存目录 |
| 本地盘符 | `D:\path\to\file.zip` | `copy` 到保存目录 |
| OSS | 仅有 `soc_version` + `parent_dir` | 按 BMC OSS 配置下载（保存目录仍优先用 `package_save_dir`） |

推荐邮件正文示例：

```text
T2_OTA
{"package_url":"\\\\192.168.1.10\\ci\\full_update.7z"}
```

```text
T2_OTA
package_path=http://192.168.1.10/ota/update.zip
```

---

## 2. 「升级包保存」路径（package_save_dir）

解析函数：`_resolve_package_save_dir()`。

| 优先级 | 配置项 | 说明 |
|--------|--------|------|
| 1 | `email.package_save_dir` | GUI「升级包保存」；推荐填本机绝对路径 |
| 2 | `download_dir` | 全局下载目录回退 |
| 3 | `./t2_downloads` | 相对 `scripts/` 的默认目录 |

规则：

- **绝对路径**：原样使用（如 `D:\ota\packages`）
- **相对路径**：相对 `scripts/` 目录解析
- 目录不存在时，下载/复制前会自动 `makedirs`

GUI 操作：

1. 在 SocOtaUpgrade 填「升级包保存」
2. 点保存/启动邮箱监听时，会同步到同级目录的 `t2_atf_config.json`
3. 监听进程读该配置，把所有邮件拿到的包落到此目录

---

## 3. 落地与 OTA 整理

### `fetch_package_to_local(source, save_dir)`

1. 按源路径猜文件名（URL path / basename）
2. HTTP → `download_package`
3. UNC / 本地文件 → `shutil.copy2` 到 `save_dir\<文件名>`
4. 若源文件已在目标目录，直接复用

### `prepare_ota_package_for_upgrade(local_path)`

把落到本地的文件整理成 `SocOtaUpgrade` 能吃的包：

| 输入 | 处理 |
|------|------|
| `.zip` | 直接返回 |
| 含 `payload.bin` 的目录 | 直接返回 |
| `.7z` / `.rar` | 调 7-Zip 解压，再找 `update.zip` / `ota.zip` / payload 目录 |
| 嵌套周包 | 最多递归 3 层 |

`.7z` 需本机有 `7z.exe`（默认找 `C:\Program Files\7-Zip\7z.exe`，或配置 `seven_zip_exe`）。

---

## 4. 相关配置一览（t2_atf_config.json）

```json
{
  "email": {
    "enabled": true,
    "protocol": "ews",
    "ews_url": "https://webmail.example.com/EWS/Exchange.asmx",
    "username": "",
    "password": "",
    "subject_keyword": "T2_OTA",
    "package_save_dir": "D:\\ota\\packages",
    "poll_interval_sec": 30,
    "mark_processed_policy": "after_attempt",
    "processed_uid_file": "./t2_email_processed_uids.json"
  },
  "upgrade_file_name": "ota.zip",
  "download_dir": "./t2_downloads",
  "keep_downloaded_package": false
}
```

| 字段 | 作用 |
|------|------|
| `package_save_dir` | **邮件包落地根目录**（本模块核心） |
| `subject_keyword` | 过滤关键字（主题或正文） |
| `protocol` | `ews`（推荐）/ `imap` / `pop3` / `auto` |
| `mark_processed_policy` | `after_attempt`（默认）或 `after_success`（失败可重试） |
| `keep_downloaded_package` | 升级结束后是否保留本地包 |

---

## 5. 启动方式

```bat
:: 仅邮箱
python t2_upgrade_entry.py --listen-email

:: TCP + 邮箱（email.enabled=true 时）
python t2_upgrade_entry.py --listen

:: GUI：SocOtaUpgrade.exe →「启动邮箱监听」
```

模拟（不真收邮件，只测下载路径链路）：

```bat
python t2_upgrade_entry.py --simulate-email "\\\\192.168.1.10\\share\\update.7z"
python t2_upgrade_entry.py --simulate-email "http://192.168.1.10/ota/update.zip" --fetch-only
```

（具体 CLI 以 `t2_upgrade_entry.py --help` 为准。）

---

## 6. 排障要点

| 现象 | 排查 |
|------|------|
| 邮件不触发 | `enabled`、关键字是否在主题/正文、账号密码、协议是否与邮箱一致 |
| 提示源文件不可访问 | UNC 权限 / 本机路径是否存在 / HTTP 是否可达 |
| `.7z` 失败 | 是否安装 7-Zip；或配置 `seven_zip_exe` |
| 包落错目录 | 看日志里的 `升级包保存目录=`；核对 GUI 与 `email.package_save_dir` 是否同步 |
| 重复升级 / 从不重试 | 看 `mark_processed_policy` 与 `t2_email_processed_uids.json` |
