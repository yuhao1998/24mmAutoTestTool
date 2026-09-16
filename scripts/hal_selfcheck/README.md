# HAL 模块自检（脚本池）

与 `hardware/<module>/`、`hal_modules.json` 的 `Id` 对齐。

## 目录约定

```text
hal_selfcheck/<module_id>/
  manifest.json   # 上架 ATF「脚本列表」元数据
  run.sh          # 车机执行入口 → 写 module_result.json
  cases/          # L2 用例（可选）
  lib/            # 四步框架等（可选）
```

公共框架：`_framework/`（L2 四步 + 快照）。L1 批量生成器：`_gen_l1_modules.py`。

## L1 覆盖（2026-08-03）

本机、无外设；测项：`ipc` / `binder` / `response` / `function_smoke` / `no_crash`（`memleak_probe` 默认 skip）。

| 探测 | 做法 |
|------|------|
| ipc | HAL 二进制是否存在 |
| binder | 进程是否存活 |
| response / smoke | HIDL：`lshal` 命中接口 FQN；守护进程：进程存活；vehicleconfig：调用 `vehicleconfigTest` 读槽 |
| no_crash | 探测后进程仍在；tombstone 名宽松过滤 |

已接入模块：`vehicleconfig`（L1+L2）、以及 `audioctrl` / `light` / `ampservice` / `localradio` / `metazone` / `input` / `drinfo` / `gnssdr` / `anc` / `someip` / `mostslave` / `installerhal` / `diag` / `devmanager` / `cameractrl` / `securitychip` / `most` / `rse` / `earlycarservice` / `maintainhal` / `vehicle`（占位）。

注册表：`scripts/t2_script_pool.json`（22 条）。

## 车机路径与执行约定（勿歧义）

| 项 | 约定 |
|----|------|
| 推送目标 | `/data/local/tmp/t2_selfcheck/<module_id>/`（整模块目录） |
| 执行 | `sh .../run.sh <remote_dir>`，`$1` / `T2_SELFCHECK_OUT` = 输出目录 |
| 产物 | 车机 `$OUT/module_result.json`；PC 拉取到 `session/detect/modules/<id>/` |
| 总报告 | PC 侧 `t2_detect_report.py` → `session/detect/detect_result.html` |

## 谁会真正执行脚本池？

| 入口 | 行为 |
|------|------|
| SocOtaUpgrade GUI「执行检查（L0+脚本池）」 | **会**：先 L0，再按 UI 同步的 `t2_script_pool.json` push→跑→收集 |
| GUI 升级成功后（勾选「L0 状态 + 脚本池」） | **会**：同上 |
| `python t2_script_pool.py --scripts-dir ... --session-dir ...` | **会**：独立 CLI（若仓库提供该入口） |
| 仅配置了「自检脚本」路径、但未点上述入口 | **不会**自动跑 |

## 手动单模块

```bash
adb push scripts/hal_selfcheck/audioctrl /data/local/tmp/t2_selfcheck/audioctrl
adb shell sh /data/local/tmp/t2_selfcheck/audioctrl/run.sh /data/local/tmp/t2_selfcheck/audioctrl
adb pull /data/local/tmp/t2_selfcheck/audioctrl/module_result.json .
```

## 上架到测试平台

```bash
python t2_atf_script_sync.py --dry-run
python t2_atf_script_sync.py
```

规范见 `docs/T2脚本池-思维导图与格式说明.md`、`docs/AI模块测试脚本生成规范.md`。
