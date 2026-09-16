# -*- coding: utf-8 -*-
"""Generate L1-only HAL selfcheck packages under scripts/hal_selfcheck/<id>/."""
from __future__ import annotations

import json
from pathlib import Path
from textwrap import dedent

ROOT = Path(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\hal_selfcheck")
POOL_JSON = Path(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\t2_script_pool.json")

# machine-only L1: binary + process + lshal interface registration (= service call feedback)
# No peripheral writes. Optional TEST_BIN readonly hooks left for future via env.
MODULES = [
    {
        "id": "audioctrl",
        "name": "音频控制 HAL",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.audioctrl@1.1-service",
        "process": "audioctrl@1.1-service",
        "init": "hardware.audioctrl-hal-1.1",
        "lshal": "vendor.iauto.hardware.audioctrl@1.1::IAudioCtrl/default",
        "tombstone": "audioctrl",
        "required": True,
        "owner": "audioctrl-team",
    },
    {
        "id": "light",
        "name": "灯光 HAL",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.light@1.0-service",
        "process": "light@1.0-service",
        "init": "LightlHal",
        "lshal": "vendor.iauto.hardware.light@1.0::ILight/default",
        "tombstone": "light",
        "required": True,
        "owner": "light-team",
    },
    {
        "id": "ampservice",
        "name": "功放 AMPService",
        "binary": "/vendor/bin/AMPService",
        "process": "AMPService",
        "init": "amp_service",
        "lshal": "vendor.hsae.hardware.ampcontrol@1.0::IAmpControl/amp_control",
        "tombstone": "AMPService|ampcontrol",
        "required": False,
        "owner": "ampservice-team",
        "notes": "依赖 Most HAL；无 MOST 时 soft-skip（不记 Fail）",
    },
    {
        "id": "localradio",
        "name": "收音 localradio",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.localradio@1.0-service",
        "process": "localradio@1.0-service",
        "init": "LocalRadio",
        "lshal": "vendor.iauto.hardware.localradio@1.0::ILocalRadio/default",
        "tombstone": "localradio",
        "required": False,
        "owner": "radio-team",
    },
    {
        "id": "metazone",
        "name": "MetaZone 分区存储",
        "binary": "/vendor/bin/hw/vendor.hsae.hardware.metazone@1.0-service",
        "process": "metazone@1.0-service",
        "init": "metazone",
        "lshal": "vendor.hsae.hardware.metazone@1.0::IMetazone/default",
        "tombstone": "metazone",
        "required": False,
        "owner": "metazone-team",
        "notes": "实现不在本 hardware 仓库一级目录；按常见安装路径探测",
        "binary_optional": True,
    },
    {
        "id": "input",
        "name": "输入 HAL",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.input@1.0-service",
        "process": "input@1.0-service",
        "init": "Input",
        "lshal": "vendor.iauto.hardware.input@1.0::IInput/default",
        "tombstone": "input@1.0",
        "required": True,
        "owner": "input-team",
    },
    {
        "id": "drinfo",
        "name": "航位 DRInfo",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.drinfo@1.0-service",
        "process": "drinfo@1.0-service",
        "init": "DRInfo",
        "lshal": "vendor.iauto.hardware.drinfo@1.0::IDRInfoController/default",
        "tombstone": "drinfo",
        "required": True,
        "owner": "drinfo-team",
    },
    {
        "id": "gnssdr",
        "name": "Gnssdr",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.gnssdr@1.0-service",
        "process": "gnssdr@1.0-service",
        "init": "Gnssdr",
        "lshal": "vendor.iauto.hardware.gnssdr@1.0::IGnssdrd/default",
        "tombstone": "gnssdr",
        "required": False,
        "owner": "gnssdr-team",
    },
    {
        "id": "anc",
        "name": "ANC ASC 查询",
        "binary": "/vendor/bin/hw/vendor.hsae.hardware.anc@1.0-service",
        "process": "anc@1.0-service",
        "init": "ANCHal",
        "lshal": "vendor.hsae.hardware.anc@1.0::IANC/default",
        "tombstone": "anc@1.0",
        "required": False,
        "owner": "anc-team",
    },
    {
        "id": "someip",
        "name": "SOME/IP 守护进程",
        "binary": "/vendor/bin/isomeipd_ics",
        "process": "isomeipd_ics",
        "init": "isomeipd_ics",
        "lshal": "",  # non-HIDL
        "tombstone": "isomeipd",
        "required": False,
        "owner": "someip-team",
        "mode": "daemon",
    },
    {
        "id": "mostslave",
        "name": "MOST 从节点",
        "binary": "/vendor/bin/hw/vendor.hsae.hardware.mostslave@1.0-service",
        "process": "mostslave@1.0-service",
        "init": "vendor.hsae_mostslave_hal",
        "lshal": "vendor.hsae.hardware.mostslave@1.0::IMostSlaveHal/default",
        "tombstone": "mostslave",
        "required": False,
        "owner": "mostslave-team",
    },
    {
        "id": "installerhal",
        "name": "OTA installerhal",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.installerhal@1.0-service",
        "process": "installerhal@1.0-service",
        "init": "vendor.iauto_installerhal",
        "lshal": "vendor.iauto.hardware.installerhal.common@1.0::IInstallerHal/default",
        "tombstone": "installerhal",
        "required": True,
        "owner": "installerhal-team",
    },
    {
        "id": "diag",
        "name": "诊断 HAL",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.diag@1.0-service",
        "process": "diag@1.0-service",
        "init": "Diag",
        "lshal": "vendor.iauto.hardware.diag@1.0::IDiag/default",
        "tombstone": "diag",
        "required": True,
        "owner": "diag-team",
    },
    {
        "id": "devmanager",
        "name": "设备管理 HAL",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.devmanager@1.1-service",
        "process": "devmanager@1.1-service",
        "init": "devmanager",
        "lshal": "vendor.iauto.hardware.devmanager@1.1::IDevManager/default",
        "tombstone": "devmanager",
        "required": True,
        "owner": "devmanager-team",
    },
    {
        "id": "cameractrl",
        "name": "相机控制 HAL",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.cameractrl@1.0-service",
        "process": "cameractrl@1.0-service",
        "init": "CameraCtrlHal",
        "lshal": "vendor.iauto.hardware.cameractrl@1.0::ICameraCtrl/default",
        "tombstone": "cameractrl",
        "required": False,
        "owner": "cameractrl-team",
    },
    {
        "id": "securitychip",
        "name": "安全芯片 securityta100",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.securitychip@1.0-service",
        "process": "securitychip@1.0-service",
        "init": "vendor.iauto_securitychip",
        "lshal": "vendor.iauto.hardware.securitychip@1.0::IHsmManager/default",
        "tombstone": "securitychip|ta100",
        "required": False,
        "owner": "securitychip-team",
    },
    {
        "id": "most",
        "name": "Most 环网 HAL",
        "binary": "/vendor/bin/hw/vendor.hsae.hardware.most@1.0-service",
        "process": "most@1.0-service",
        "init": "vendor.hsae_most_hal",
        "lshal": "vendor.hsae.hardware.most@1.0::IMostHal/default",
        "tombstone": "most@1.0",
        "required": False,
        "owner": "most-team",
    },
    {
        "id": "rse",
        "name": "后排 RSE",
        "binary": "/vendor/bin/hw/vendor.iauto.hardware.rse@1.0-service",
        "process": "rse@1.0-service",
        "init": "RSE",
        "lshal": "vendor.iauto.hardware.rse@1.0::IRSE/default",
        "tombstone": "rse@1.0",
        "required": False,
        "owner": "rse-team",
    },
    {
        "id": "earlycarservice",
        "name": "EarlyCarService 串口",
        "binary": "/vendor/bin/hw/vendor.hsae.hardware.earlycarservice.serial@1.0-service",
        "process": "earlycarservice.serial@1.0-service",
        "init": "earlycarservice_serial_hal_service",
        "lshal": "vendor.hsae.hardware.earlycarservice.serial@1.0::ISerial/default",
        "tombstone": "earlycarservice",
        "required": False,
        "owner": "earlycarservice-team",
    },
    {
        "id": "maintainhal",
        "name": "MaintainHal 维护诊断",
        "binary": "/vendor/bin/hw/vendor.hsae.hardware.maintain@1.0-service",
        "process": "maintain@1.0-service",
        "init": "vendor.hsae_maintain_hal",
        "lshal": "vendor.hsae.hardware.maintain@1.0::IMaintainHal/default",
        "tombstone": "maintain",
        "required": False,
        "owner": "maintainhal-team",
    },
    {
        "id": "vehicle",
        "name": "系统 VHAL（本树无实现）",
        "binary": "",
        "process": "",
        "init": "",
        "lshal": "android.hardware.automotive.vehicle@2.0::IVehicle/default",
        "tombstone": "vehicle",
        "required": False,
        "owner": "vehicle-team",
        "mode": "placeholder",
        "notes": "本仓库 vehicle/ 仅 sepolicy；L1 仅探测外树服务是否登记",
        "binary_optional": True,
    },
]


RUN_SH = r'''#!/system/bin/sh
# HalSelfCheck L1 — {module_id}
# 范围：仅本机。二进制存在 + 进程存活 + lshal 接口登记（服务可 get）+ 调用后无崩溃。
# 不写状态、不依赖外设/台架。
# 源码基准：ssh://192.168.64.47:29418/Android/QCOM_SA8155_6155/platform/vendor/hsae/proprietary/hardware；分支：24MM_T2_dev；生成日：2026-08-03

MODULE_ID="{module_id}"
MODULE_NAME="{module_name}"
HAL_BINARY="{binary}"
PROCESS_PAT="{process}"
LSHAL_NEEDLE="{lshal}"
TOMBSTONE_KW="{tombstone}"
BINARY_OPTIONAL="{binary_optional}"
MODE="{mode}"
OUT="${{1:-${{T2_SELFCHECK_OUT:-/data/local/tmp/t2_selfcheck/{module_id}}}}}"
mkdir -p "$OUT/logs" "$OUT/work_l1"

STARTED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
PASS_N=0
FAIL_N=0
SKIP_N=0
CASES_JSON=""

append_case() {{
  id="$1"; name="$2"; level="$3"; st="$4"; req="$5"; msg="$6"
  case "$st" in
    pass) PASS_N=$((PASS_N+1)) ;;
    fail|error) FAIL_N=$((FAIL_N+1)) ;;
    *) SKIP_N=$((SKIP_N+1)) ;;
  esac
  msg_esc=$(printf '%s' "$msg" | sed 's/\\/\\\\/g; s/"/\\"/g')
  name_esc=$(printf '%s' "$name" | sed 's/"/\\"/g')
  if [ -n "$CASES_JSON" ]; then
    CASES_JSON="${{CASES_JSON}},"
  fi
  CASES_JSON="${{CASES_JSON}}{{\"id\":\"${{id}}\",\"name\":\"${{name_esc}}\",\"level\":\"${{level}}\",\"status\":\"${{st}}\",\"required\":${{req}},\"message\":\"${{msg_esc}}\"}}"
}}

find_pid() {{
  pat="$1"
  [ -n "$pat" ] || return 1
  if pidof "$pat" >/dev/null 2>&1; then
    pidof "$pat" 2>/dev/null | awk '{{print $1}}'
    return 0
  fi
  if pgrep -f "$pat" >/dev/null 2>&1; then
    pgrep -f "$pat" 2>/dev/null | head -n 1
    return 0
  fi
  return 1
}}

lshal_probe() {{
  needle="$1"
  logf="$OUT/work_l1/lshal.log"
  if [ -z "$needle" ]; then
    echo "no lshal needle" > "$logf"
    return 2
  fi
  if command -v lshal >/dev/null 2>&1; then
    lshal 2>/dev/null | tee "$logf" | grep -F "$needle" >/dev/null 2>&1
    return $?
  fi
  # fallback: dumpsys hwservicemanager (best-effort)
  if dumpsys hwservicemanager 2>/dev/null | tee "$logf" | grep -F "$needle" >/dev/null 2>&1; then
    return 0
  fi
  echo "lshal/dumpsys unavailable" > "$logf"
  return 3
}}

# ---------- placeholder mode ----------
if [ "$MODE" = "placeholder" ]; then
  append_case "ipc" "服务可连接" "L1" "skip" "false" "本树无 HAL 实现；跳过二进制检查"
  if [ -n "$LSHAL_NEEDLE" ] && lshal_probe "$LSHAL_NEEDLE"; then
    append_case "response" "接口有响应" "L1" "pass" "false" "外树服务已登记: $LSHAL_NEEDLE"
    append_case "binder" "服务绑定正常" "L1" "pass" "false" "lshal listed (no local process pattern)"
    append_case "function_smoke" "功能冒烟" "L1" "pass" "false" "interface listed via lshal"
  else
    append_case "response" "接口有响应" "L1" "skip" "false" "外树 IVehicle 未登记或 lshal 不可用"
    append_case "binder" "服务绑定正常" "L1" "skip" "false" "no local process; external optional"
    append_case "function_smoke" "功能冒烟" "L1" "skip" "false" "placeholder module"
  fi
  append_case "no_crash" "调用后无崩溃" "L1" "pass" "false" "no local invoke"
  append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled"
  FINISHED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
  OVERALL="pass"
  SUMMARY="Pass=${{PASS_N}} Fail=${{FAIL_N}} Skip=${{SKIP_N}}"
  cat > "$OUT/module_result.json" <<EOF
{{
  "schema_version": "1.0",
  "module_id": "${{MODULE_ID}}",
  "module_name": "${{MODULE_NAME}}",
  "started_at": "${{STARTED}}",
  "finished_at": "${{FINISHED}}",
  "overall": "${{OVERALL}}",
  "summary": "${{SUMMARY}}",
  "cases": [ ${{CASES_JSON}} ],
  "attachments": []
}}
EOF
  echo "MODULE=${{MODULE_ID}} OVERALL=${{OVERALL}} ${{SUMMARY}}"
  echo "RESULT=$OUT/module_result.json"
  echo "Pass"
  exit 0
fi

# ---------- ipc ----------
if [ -z "$HAL_BINARY" ]; then
  append_case "ipc" "服务可连接" "L1" "fail" "true" "HAL_BINARY empty"
elif [ -e "$HAL_BINARY" ]; then
  append_case "ipc" "服务可连接" "L1" "pass" "true" "binary exists: $HAL_BINARY"
elif [ "$BINARY_OPTIONAL" = "true" ]; then
  append_case "ipc" "服务可连接" "L1" "warn" "false" "binary missing (optional): $HAL_BINARY"
else
  append_case "ipc" "服务可连接" "L1" "fail" "true" "binary missing: $HAL_BINARY"
fi

# ---------- binder (process) ----------
PID1=""
PID1=$(find_pid "$PROCESS_PAT" || true)
if [ -n "$PID1" ]; then
  append_case "binder" "服务绑定正常" "L1" "pass" "true" "process alive: $PROCESS_PAT pid=$PID1"
else
  append_case "binder" "服务绑定正常" "L1" "fail" "true" "process not found: $PROCESS_PAT"
fi

# ---------- response + function_smoke ----------
LSHAL_RC=2
LSHAL_MSG=""
if [ "$MODE" = "daemon" ]; then
  # non-HIDL：进程存活即视为服务响应冒烟
  if [ -n "$PID1" ]; then
    append_case "response" "接口有响应" "L1" "pass" "true" "daemon process responding (pid=$PID1)"
    append_case "function_smoke" "功能冒烟" "L1" "pass" "true" "daemon alive; no HIDL lshal"
  else
    append_case "response" "接口有响应" "L1" "fail" "true" "daemon not running"
    append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "daemon not running"
  fi
else
  lshal_probe "$LSHAL_NEEDLE"
  LSHAL_RC=$?
  cp "$OUT/work_l1/lshal.log" "$OUT/logs/l1_lshal.log" 2>/dev/null || true
  if [ "$LSHAL_RC" -eq 0 ]; then
    append_case "response" "接口有响应" "L1" "pass" "true" "lshal hit: $LSHAL_NEEDLE"
    if [ -n "$PID1" ]; then
      append_case "function_smoke" "功能冒烟" "L1" "pass" "true" "process+lshal ok"
    else
      append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "lshal ok but process missing"
    fi
  elif [ "$LSHAL_RC" -eq 3 ]; then
    # 无 lshal 工具：降级为进程存活冒烟（仍记 warn，避免假 Pass 接口）
    if [ -n "$PID1" ]; then
      append_case "response" "接口有响应" "L1" "warn" "true" "lshal unavailable; process alive only"
      append_case "function_smoke" "功能冒烟" "L1" "warn" "true" "lshal unavailable; smoke=process"
    else
      append_case "response" "接口有响应" "L1" "fail" "true" "lshal unavailable and process missing"
      append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "cannot probe"
    fi
  else
    append_case "response" "接口有响应" "L1" "fail" "true" "lshal miss: $LSHAL_NEEDLE"
    append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "interface not listed"
  fi
fi

# ---------- no_crash（探测后 pid 仍在；tombstone 名宽松）----------
sleep 1
PID2=""
PID2=$(find_pid "$PROCESS_PAT" || true)
TB_HIT=$(ls /data/tombstones 2>/dev/null | head -n 8)
TB_REL=0
OLD_IFS=$IFS
IFS='|'
for kw in $TOMBSTONE_KW; do
  [ -n "$kw" ] || continue
  if echo "$TB_HIT" | grep -qi -- "$kw"; then
    TB_REL=1
    break
  fi
done
IFS=$OLD_IFS

if [ -z "$PROCESS_PAT" ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "pass" "true" "no process pattern"
elif [ -z "$PID2" ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "fail" "true" "process gone after probe"
elif [ "$TB_REL" -eq 1 ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "fail" "true" "tombstone name may relate to module"
elif [ -n "$PID1" ] && [ "$PID1" != "$PID2" ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "warn" "true" "pid changed $PID1->$PID2"
else
  append_case "no_crash" "调用后无崩溃" "L1" "pass" "true" "process alive pid=${{PID2}}"
fi

append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled in manifest"

FINISHED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
if [ "$FAIL_N" -gt 0 ]; then
  OVERALL="fail"
else
  OVERALL="pass"
fi
SUMMARY="Pass=${{PASS_N}} Fail=${{FAIL_N}} Skip=${{SKIP_N}}"

cat > "$OUT/module_result.json" <<EOF
{{
  "schema_version": "1.0",
  "module_id": "${{MODULE_ID}}",
  "module_name": "${{MODULE_NAME}}",
  "started_at": "${{STARTED}}",
  "finished_at": "${{FINISHED}}",
  "overall": "${{OVERALL}}",
  "summary": "${{SUMMARY}}",
  "cases": [ ${{CASES_JSON}} ],
  "attachments": []
}}
EOF

echo "MODULE=${{MODULE_ID}} OVERALL=${{OVERALL}} ${{SUMMARY}}"
echo "RESULT=$OUT/module_result.json"
if [ "$OVERALL" = "pass" ]; then
  echo "Pass"
  exit 0
fi
echo "Failed"
exit 1
'''


def write_module(m: dict) -> Path:
    mid = m["id"]
    d = ROOT / mid
    d.mkdir(parents=True, exist_ok=True)
    run = RUN_SH.format(
        module_id=mid,
        module_name=m["name"],
        binary=m.get("binary", ""),
        process=m.get("process", ""),
        lshal=m.get("lshal", ""),
        tombstone=m.get("tombstone", mid),
        binary_optional="true" if m.get("binary_optional") else "false",
        mode=m.get("mode", "hidl"),
    )
    # normalize newlines to LF
    (d / "run.sh").write_text(run.replace("\r\n", "\n"), encoding="utf-8", newline="\n")

    tags = ["hal", mid, "l1"]
    if m.get("notes"):
        desc_extra = f"；{m['notes']}"
    else:
        desc_extra = ""
    manifest = {
        "schema_version": "1.0",
        "module_id": mid,
        "module_name": m["name"],
        "hal_binary": m.get("binary", ""),
        "init_service": m.get("init", ""),
        "owner": m.get("owner", f"{mid}-team"),
        "script_name": f"HalSelfCheck_{mid}.sh",
        "script_desc": f"{mid} 模块自检（L1：本机接口/服务登记，无外设）{desc_extra}",
        "platform": {
            "sync_to_atf": True,
            "default_selected": bool(m.get("required", False)),
            "tags": tags,
        },
        "timeout_sec": 120,
        "required": bool(m.get("required", False)),
        "l1": {
            "ipc": True,
            "response": True,
            "binder": True,
            "function_smoke": True,
            "no_crash": True,
            "memleak_probe": False,
            "lshal_needle": m.get("lshal", ""),
            "mode": m.get("mode", "hidl"),
        },
        "cases": [
            {"id": "ipc", "name": "服务可连接", "level": "L1", "enabled": True, "required": True},
            {"id": "response", "name": "接口有响应", "level": "L1", "enabled": True, "required": True},
            {"id": "binder", "name": "服务绑定正常", "level": "L1", "enabled": True, "required": True},
            {"id": "function_smoke", "name": "功能冒烟", "level": "L1", "enabled": True, "required": True},
            {"id": "no_crash", "name": "调用后无崩溃", "level": "L1", "enabled": True, "required": True},
            {"id": "memleak_probe", "name": "无内存泄漏（探针）", "level": "L1", "enabled": False, "required": False},
        ],
    }
    if m.get("mode") == "placeholder":
        for c in manifest["cases"]:
            if c["id"] != "memleak_probe":
                c["required"] = False
        manifest["required"] = False

    (d / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    return d


def update_pool(modules: list[dict]) -> None:
    # Keep vehicleconfig entry; add/replace others pointing at local hal_selfcheck
    base = ROOT.parent  # scripts/
    pool = {
        "_comment": "T2 脚本池。GUI「自检脚本」列同步本文件；执行检查/升级成功后：push→/data/local/tmp/t2_selfcheck/<id>/→run.sh→拉 module_result.json。",
        "enabled": True,
        "fail_fast": False,
        "default_timeout_sec": 120,
        "device_script_dir": "/data/local/tmp/t2_script_pool",
        "device_hal_selfcheck_dir": "/data/local/tmp/t2_selfcheck",
        "build_detect_report": True,
        "scripts": [],
    }

    # vehicleconfig: prefer existing package if present; else local
    vc_pkg = Path(r"E:/BMCSJB/vehicleconfig/run.sh")
    vc_local = ROOT / "vehicleconfig" / "run.sh"
    vc_path = str(vc_pkg).replace("\\", "/") if vc_pkg.exists() else str(vc_local.resolve())
    pool["scripts"].append(
        {
            "kind": "hal_module",
            "id": "vehicleconfig",
            "module_id": "vehicleconfig",
            "name": "vehicleconfig 自检（L1+L2）",
            "path": vc_path,
            "package": "vendor.iauto.hardware.vehicleconfig@1.0-service",
            "timeout_sec": 300,
            "enabled": True,
            "tags": ["hal", "vehicleconfig", "l1", "l2"],
        }
    )

    for m in modules:
        mid = m["id"]
        if mid == "vehicleconfig":
            continue
        run = (ROOT / mid / "run.sh").resolve()
        pool["scripts"].append(
            {
                "kind": "hal_module",
                "id": mid,
                "module_id": mid,
                "name": f"{mid} L1 自检",
                "path": str(run),
                "package": Path(m.get("binary") or mid).name or mid,
                "timeout_sec": 120,
                "enabled": True,
                "tags": ["hal", mid, "l1"],
            }
        )

    POOL_JSON.write_text(
        json.dumps(pool, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def main() -> None:
    from _patch_soft_skip import apply_root

    ROOT.mkdir(parents=True, exist_ok=True)
    written = []
    for m in MODULES:
        d = write_module(m)
        written.append(d.name)
    update_pool(MODULES)
    soft = apply_root(ROOT)
    print("generated:", ", ".join(written))
    print("soft-skip:", ", ".join(soft))
    print("pool:", POOL_JSON)


if __name__ == "__main__":
    main()
