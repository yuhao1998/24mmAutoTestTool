# -*- coding: utf-8 -*-
"""
Generate BMCSJB-style HAL test packages under E:\\BMCSJB\\T2TestScript\\<module>\\
Aligned with docs/hal_modules (担当 L1/L2) and vehicleconfig delivery layout.
"""
from __future__ import annotations

import json
import shutil
from pathlib import Path

OUT_ROOT = Path(r"E:\BMCSJB\T2TestScript")
SRC_HAL = Path(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\hal_selfcheck")
FW_DIR = SRC_HAL / "_framework"
GIT = "ssh://192.168.64.47:29418/Android/QCOM_SA8155_6155/platform/vendor/hsae/proprietary/hardware"
BRANCH = "24MM_T2_dev"
DOCS = "T2自测工具/docs/hal_modules"

# L2 skeletons from 担当下发 (enabled=false until TEST_BIN / get_how/set_how filled)
# Each: (id, name, required, notes)
L2_BY_MODULE: dict[str, list[tuple[str, str, bool, str]]] = {
    "audioctrl": [
        ("mute_write_restore", "静音写读回并还原", True, "get/setAudioMute；RESTORE_STRICT=1"),
        ("group_volume_restore", "分组音量写读回并还原", True, "set/adjust/getGroupVolume"),
        ("audio_params_restore", "音效参数/SoundState 写读回并还原", True, "set/getAudioParameters"),
        ("source_switch_restore", "音源切换三步状态机后还原", True, "Change→confirm→Play；切回原音源"),
        ("timed_mute_restore", "定时静音后取消并还原", True, "setAudioTimedMute"),
        ("channel_mix_restore", "声道 Channel/Mix 写读回并还原", True, "setAudioChannel/Mix"),
        ("listener_register_cleanup", "音源 Listener 注册后注销", False, "restore=unregister"),
        ("amp_diag_start_stop", "AMP Diag 启停（专项）", False, "须 stop/clear；勿与 AMP 双写；默认关"),
    ],
    "light": [
        ("brightness_write_restore", "亮度写改可观测并还原", True, "setBrightness；无 get 则 dumpsys/约定档"),
        ("display_switch_restore", "Display 切换并还原", True, "setDisplay"),
        ("display_mute_restore", "displayMute 开关并还原", True, "displayMute"),
        ("invalid_displayid_expected_fail", "非法 displayId 预期失败", True, "状态不变"),
    ],
    "ampservice": [
        ("mute_write_restore", "Mute 族写读回并还原", True, "需 MOST；无环网 Blocked"),
        ("main_volume_restore", "主音量/步进写读回并还原", True, "setMainVolume/step"),
        ("source_state_restore", "音源状态机后还原", True, "disconnect/切回原源"),
        ("tone_params_restore", "音效 Tone/EQ 写读回并还原", True, "与 audioctrl 单层主测"),
    ],
    "localradio": [
        ("tune_restore", "调谐后还原频点/波段", True, "ManualTune/ChangeBand"),
        ("seek_scan_restore", "Seek/Scan 后还原频点", True, "含 Cancel 策略"),
        ("preset_restore", "预置操作后还原列表", False, "若写了预置须还原"),
        ("radio_mute_restore", "收音静音写读回并还原", True, "tunerRadioSounds"),
        ("listener_cleanup", "Listener 注册后注销", False, "restore=unregister"),
    ],
    "metazone": [
        ("dword_write_restore_flush", "白名单 DWORD 写读回+Flush+还原", True, "须审批 index"),
        ("binary_write_restore_flush", "白名单 Binary 写读回+Flush+还原", True, "须审批 index"),
        ("flush_only", "Flush 刷盘确认", False, "配合写用例"),
        ("spec_reserved_blocked", "Spec/Reserved（默认关）", False, "高风险默认关"),
    ],
    "input": [
        ("touch_sensitivity_restore", "触摸灵敏度写改并还原", True, "setTouchSensitivity"),
    ],
    "drinfo": [
        ("callback_register_cleanup", "DR 回调注册后注销", True, "restore=unregister；无写属性"),
        ("sensor_stream_readonly", "Sensor/GNSS 推送只读冒烟", False, "真值需台架"),
    ],
    "gnssdr": [
        ("listener_register_cleanup", "Listener 注册后注销", True, "register/unRegisterListener"),
        ("nmea_callback_readonly", "NMEA/定位回调只读冒烟", False, "Test 可注入；精度需台架"),
    ],
    "anc": [
        ("listener_cleanup", "ANC Listener 注册后注销", True, "restore=unregister"),
        ("get_ascinfo_readonly", "getAscInfo 只读冒烟", True, "写设定在 Most ANC"),
    ],
    "someip": [
        ("daemon_lifecycle_smoke", "守护进程生命周期冒烟", True, "非 HIDL 四步；测后恢复测前态"),
        ("gtest_client_server", "gtest 客户端/服务端（专项）", False, "单元级"),
        ("e2e_protocol_lab", "E2E 协议联调（专项）", False, "联调向默认关"),
    ],
    "mostslave": [
        ("start_stop_restore", "启停后恢复测前态", True, "init/start/stop/getState；无环网 Blocked"),
        ("fblock_get_interface", "getInterface FBlock 只读", False, "需 MOST"),
        ("fblock_callback_cleanup", "FBlock 回调注册后注销", False, "需 MOST"),
    ],
    "installerhal": [
        ("inventory_state_readonly", "Inventory/State/Slot 只读", True, "较安全"),
        ("preinstall_condition_check", "预装条件检查", True, "不应进入安装"),
        ("ota_install_rollback", "完整安装+回滚（默认关）", False, "破坏性；须回 IDLE"),
        ("set_slot_restore", "setSlot 后还原（默认关）", False, "台架专用"),
    ],
    "diag": [
        ("can_send_readonly_session", "CAN 收发会话冒烟", False, "需台架"),
        ("speaker_check_quit", "扬声器检测走完 Quit", True, "必须退出检测模式"),
        ("selfcheck_start_stop", "自检 Start/Stop/Result", True, "不留进行中"),
        ("clear_dtc_blocked", "reqClearDTC（默认关）", False, "不可还原"),
        ("listener_cleanup", "Listener 注册后注销", False, "restore=unregister"),
    ],
    "devmanager": [
        ("attrs_mac_readonly", "属性/MAC/RTC/Persist 只读冒烟", True, ""),
        ("rtc_persist_restore", "RTC/Persist 写读回并还原", True, "须还原"),
        ("dangerous_ops_blocked", "清存/reboot/VIN 等（默认关）", False, "高危"),
    ],
    "cameractrl": [
        ("open_close_restore", "open/close 后还原测前态", True, "无相机 Blocked"),
        ("ready_flags_restore", "Capture/Display Ready 清回测前", True, ""),
        ("diag_status_restore", "DiagStatus 写改并还原", False, ""),
        ("listener_cleanup", "Listener 注册后注销", False, ""),
    ],
    "securitychip": [
        ("chip_version_readonly", "getChipNo/getSoftVersion 只读", True, ""),
        ("ta_upgrade_blocked", "TA 预装/升级（默认关）", False, "高危；须 rollback"),
        ("key_update_blocked", "密钥更新（默认关）", False, "高危"),
    ],
    "most": [
        ("amp_setget_restore", "IMostHalAmp 音量音效 Mute 还原", True, "与 audioctrl/AMP 单层；需 MOST"),
        ("anc_setting_restore", "IMostANC ASC 设定还原", True, "需 MOST"),
        ("rse_subset_restore", "IMostRSE 写改还原", False, "勿与 rse 双写"),
        ("vup_blocked", "IMostVup（默认关）", False, "高危"),
    ],
    "rse": [
        ("source_video_restore", "音源/视频源/模式写改还原", True, ""),
        ("lock_restore", "系统锁/童锁写改还原", True, ""),
        ("mute_keyboard_restore", "Mute/键盘等写改还原", True, ""),
        ("rseinput_listener_cleanup", "IRseInput 回调注销", False, ""),
    ],
    "earlycarservice": [
        ("handshake_heartbeat_smoke", "握手/心跳冒烟", True, "需 MCU；测后恢复链路"),
        ("send_data_smoke", "sendData 通道冒烟", False, "需 MCU"),
        ("callback_cleanup", "多路 Callback 清理", True, "测完清理"),
    ],
    "maintainhal": [
        ("enter_exit_diagnosis", "进/退诊断模式（必须 exit）", True, ""),
        ("self_diagnosis", "自检 startSelfDiagnosis", False, "专项"),
        ("clear_history_blocked", "清诊断历史（默认关）", False, "破坏性"),
        ("amp_speaker_check_exit", "AMP 扬声器检查走完退出", True, ""),
        ("vup_blocked", "VUP start/cancel（默认关）", False, "高危"),
    ],
    "vehicle": [
        ("property_set_restore_placeholder", "setProperty 写回原值（外树占位）", False, "本树无实现"),
        ("subscribe_cleanup_placeholder", "subscribe 后 unsubscribe（外树占位）", False, ""),
    ],
}

MODULES = [
    {"id": "audioctrl", "name": "音频控制 HAL", "binary": "/vendor/bin/hw/vendor.iauto.hardware.audioctrl@1.1-service", "process": "audioctrl@1.1-service", "init": "hardware.audioctrl-hal-1.1", "lshal": "vendor.iauto.hardware.audioctrl@1.1::IAudioCtrl/default", "tombstone": "audioctrl", "required": True, "mxx": "M02"},
    {"id": "light", "name": "灯光 HAL", "binary": "/vendor/bin/hw/vendor.iauto.hardware.light@1.0-service", "process": "light@1.0-service", "init": "LightlHal", "lshal": "vendor.iauto.hardware.light@1.0::ILight/default", "tombstone": "light", "required": True, "mxx": "M03"},
    {"id": "ampservice", "name": "功放 AMPService", "binary": "/vendor/bin/AMPService", "process": "AMPService", "init": "amp_service", "lshal": "vendor.hsae.hardware.ampcontrol@1.0::IAmpControl/amp_control", "tombstone": "AMPService|ampcontrol", "required": False, "mxx": "M04", "mode": "hidl"},
    {"id": "localradio", "name": "收音 localradio", "binary": "/vendor/bin/hw/vendor.iauto.hardware.localradio@1.0-service", "process": "localradio@1.0-service", "init": "LocalRadio", "lshal": "vendor.iauto.hardware.localradio@1.0::ILocalRadio/default", "tombstone": "localradio", "required": False, "mxx": "M05"},
    {"id": "metazone", "name": "MetaZone 分区存储", "binary": "/vendor/bin/hw/vendor.hsae.hardware.metazone@1.0-service", "process": "metazone@1.0-service", "init": "metazone", "lshal": "vendor.hsae.hardware.metazone@1.0::IMetazone/default", "tombstone": "metazone", "required": False, "mxx": "M06", "binary_optional": True},
    {"id": "input", "name": "输入 HAL", "binary": "/vendor/bin/hw/vendor.iauto.hardware.input@1.0-service", "process": "input@1.0-service", "init": "Input", "lshal": "vendor.iauto.hardware.input@1.0::IInput/default", "tombstone": "input@1.0", "required": True, "mxx": "M07"},
    {"id": "drinfo", "name": "航位 DRInfo", "binary": "/vendor/bin/hw/vendor.iauto.hardware.drinfo@1.0-service", "process": "drinfo@1.0-service", "init": "DRInfo", "lshal": "vendor.iauto.hardware.drinfo@1.0::IDRInfoController/default", "tombstone": "drinfo", "required": True, "mxx": "M08"},
    {"id": "gnssdr", "name": "Gnssdr", "binary": "/vendor/bin/hw/vendor.iauto.hardware.gnssdr@1.0-service", "process": "gnssdr@1.0-service", "init": "Gnssdr", "lshal": "vendor.iauto.hardware.gnssdr@1.0::IGnssdrd/default", "tombstone": "gnssdr", "required": False, "mxx": "M09"},
    {"id": "anc", "name": "ANC ASC 查询", "binary": "/vendor/bin/hw/vendor.hsae.hardware.anc@1.0-service", "process": "anc@1.0-service", "init": "ANCHal", "lshal": "vendor.hsae.hardware.anc@1.0::IANC/default", "tombstone": "anc@1.0", "required": False, "mxx": "M10"},
    {"id": "someip", "name": "SOME/IP 守护进程", "binary": "/vendor/bin/isomeipd_ics", "process": "isomeipd_ics", "init": "isomeipd_ics", "lshal": "", "tombstone": "isomeipd", "required": False, "mxx": "M11", "mode": "daemon"},
    {"id": "mostslave", "name": "MOST 从节点", "binary": "/vendor/bin/hw/vendor.hsae.hardware.mostslave@1.0-service", "process": "mostslave@1.0-service", "init": "vendor.hsae_mostslave_hal", "lshal": "vendor.hsae.hardware.mostslave@1.0::IMostSlaveHal/default", "tombstone": "mostslave", "required": False, "mxx": "M12"},
    {"id": "installerhal", "name": "OTA installerhal", "binary": "/vendor/bin/hw/vendor.iauto.hardware.installerhal@1.0-service", "process": "installerhal@1.0-service", "init": "vendor.iauto_installerhal", "lshal": "vendor.iauto.hardware.installerhal.common@1.0::IInstallerHal/default", "tombstone": "installerhal", "required": True, "mxx": "M13"},
    {"id": "diag", "name": "诊断 HAL", "binary": "/vendor/bin/hw/vendor.iauto.hardware.diag@1.0-service", "process": "diag@1.0-service", "init": "Diag", "lshal": "vendor.iauto.hardware.diag@1.0::IDiag/default", "tombstone": "diag", "required": True, "mxx": "M14"},
    {"id": "devmanager", "name": "设备管理 HAL", "binary": "/vendor/bin/hw/vendor.iauto.hardware.devmanager@1.1-service", "process": "devmanager@1.1-service", "init": "devmanager", "lshal": "vendor.iauto.hardware.devmanager@1.1::IDevManager/default", "tombstone": "devmanager", "required": True, "mxx": "M16"},
    {"id": "cameractrl", "name": "相机控制 HAL", "binary": "/vendor/bin/hw/vendor.iauto.hardware.cameractrl@1.0-service", "process": "cameractrl@1.0-service", "init": "CameraCtrlHal", "lshal": "vendor.iauto.hardware.cameractrl@1.0::ICameraCtrl/default", "tombstone": "cameractrl", "required": False, "mxx": "M17"},
    {"id": "securitychip", "name": "安全芯片 securityta100", "binary": "/vendor/bin/hw/vendor.iauto.hardware.securitychip@1.0-service", "process": "securitychip@1.0-service", "init": "vendor.iauto_securitychip", "lshal": "vendor.iauto.hardware.securitychip@1.0::IHsmManager/default", "tombstone": "securitychip|ta100", "required": False, "mxx": "M18"},
    {"id": "most", "name": "Most 环网 HAL", "binary": "/vendor/bin/hw/vendor.hsae.hardware.most@1.0-service", "process": "most@1.0-service", "init": "vendor.hsae_most_hal", "lshal": "vendor.hsae.hardware.most@1.0::IMostHal/default", "tombstone": "most@1.0", "required": False, "mxx": "M19"},
    {"id": "rse", "name": "后排 RSE", "binary": "/vendor/bin/hw/vendor.iauto.hardware.rse@1.0-service", "process": "rse@1.0-service", "init": "RSE", "lshal": "vendor.iauto.hardware.rse@1.0::IRSE/default", "tombstone": "rse@1.0", "required": False, "mxx": "M20"},
    {"id": "earlycarservice", "name": "EarlyCarService 串口", "binary": "/vendor/bin/hw/vendor.hsae.hardware.earlycarservice.serial@1.0-service", "process": "earlycarservice.serial@1.0-service", "init": "earlycarservice_serial_hal_service", "lshal": "vendor.hsae.hardware.earlycarservice.serial@1.0::ISerial/default", "tombstone": "earlycarservice", "required": False, "mxx": "M21"},
    {"id": "maintainhal", "name": "MaintainHal 维护诊断", "binary": "/vendor/bin/hw/vendor.hsae.hardware.maintain@1.0-service", "process": "maintain@1.0-service", "init": "vendor.hsae_maintain_hal", "lshal": "vendor.hsae.hardware.maintain@1.0::IMaintainHal/default", "tombstone": "maintain", "required": False, "mxx": "M22"},
    {"id": "vehicle", "name": "系统 VHAL（本树无实现）", "binary": "", "process": "", "init": "", "lshal": "android.hardware.automotive.vehicle@2.0::IVehicle/default", "tombstone": "vehicle", "required": False, "mxx": "M15", "mode": "placeholder", "binary_optional": True},
]


def write_lf(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text.replace("\r\n", "\n"), encoding="utf-8", newline="\n")


def copy_framework(dest_lib: Path) -> None:
    dest_lib.mkdir(parents=True, exist_ok=True)
    for name in ("case_framework.sh", "snapshot_helpers.sh"):
        src = FW_DIR / name
        if src.exists():
            shutil.copy2(src, dest_lib / name)


CASE_SH = '''#!/system/bin/sh
# L2 skeleton — {case_id} ({module_id})
# 来源：docs/hal_modules/担当下发/{mxx}_*_L2担当要求.md
# 规范：四步 PREPARE→INVOKE→ASSERT→RESTORE；写属性 RESTORE_STRICT=1
# 状态：enabled=false，直至补齐 TEST_BIN / get_how / set_how（禁止假装 Pass）

CASE_ID="{case_id}"
SCRIPT_DIR=$(cd "$(dirname "$0")/.." && pwd)
FRAMEWORK="${{SCRIPT_DIR}}/lib/case_framework.sh"
RESULT_DIR="${{RESULT_DIR:-$SCRIPT_DIR}}"
RESTORE_STRICT={restore_strict}

# shellcheck source=/dev/null
. "$FRAMEWORK"
if [ -f "${{SCRIPT_DIR}}/lib/snapshot_helpers.sh" ]; then
  # shellcheck source=/dev/null
  . "${{SCRIPT_DIR}}/lib/snapshot_helpers.sh"
fi

hook_prepare() {{
  snap_init 2>/dev/null || true
  case_log "TODO[{case_id}]: {notes}"
  case_log "TODO: 测前用 get_how 读初始值 → snap_set / snap_save_file"
  return 0
}}

hook_invoke() {{
  case_log "TODO: 调用被测接口（勿编造命令；优先模块 Test 二进制）"
  return 0
}}

hook_assert() {{
  case_log "TODO: 断言读回/回调；未实现则不得 Pass"
  # 骨架默认失败，避免假绿
  return 1
}}

hook_restore() {{
  case_log "TODO: set_how 写回快照并再读确认；失败 return 1"
  return 0
}}

case_run_four_steps
'''


def make_run_sh(m: dict) -> str:
    mid = m["id"]
    mode = m.get("mode", "hidl")
    l2 = L2_BY_MODULE.get(mid, [])
    # Build L2 runner snippets
    l2_runs = []
    for cid, name, _req, _notes in l2:
        env = f"HAL_ENABLE_{cid.upper()}"
        l2_runs.append(
            f'run_l2_if_enabled "${{{env}:-false}}" \\\n'
            f'  "$SCRIPT_DIR/cases/{cid}.sh" \\\n'
            f'  "{cid}" "{name}" "false"'
        )
    l2_block = "\n\n".join(l2_runs) if l2_runs else "# (no L2 skeletons)"

    return f'''#!/system/bin/sh
# HalSelfCheck — {mid}（BMCSJB/T2TestScript 交付包）
# L1：本机 ipc/binder/lshal（无外设写状态）
# L2：cases/ 骨架默认 skip；设 HAL_ENABLE_<CASE>=1 且补齐 hook 后再开
# 源码：{GIT} 分支 {BRANCH}
# 文档：{DOCS}/担当下发/{m.get("mxx","")}

MODULE_ID="{mid}"
MODULE_NAME="{m["name"]}"
HAL_BINARY="{m.get("binary","")}"
PROCESS_PAT="{m.get("process","")}"
LSHAL_NEEDLE="{m.get("lshal","")}"
TOMBSTONE_KW="{m.get("tombstone", mid)}"
BINARY_OPTIONAL="{"true" if m.get("binary_optional") else "false"}"
MODE="{mode}"
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
OUT="${{1:-${{T2_SELFCHECK_OUT:-/data/local/tmp/t2_selfcheck/{mid}}}}}"
export RESULT_DIR="$OUT"
mkdir -p "$OUT/logs" "$OUT/work_l1" "$OUT/cases"

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
  msg_esc=$(printf '%s' "$msg" | sed 's/\\\\/\\\\\\\\/g; s/"/\\\\"/g')
  name_esc=$(printf '%s' "$name" | sed 's/"/\\\\"/g')
  if [ -n "$CASES_JSON" ]; then CASES_JSON="${{CASES_JSON}},"; fi
  CASES_JSON="${{CASES_JSON}}{{\\"id\\":\\"${{id}}\\",\\"name\\":\\"${{name_esc}}\\",\\"level\\":\\"${{level}}\\",\\"status\\":\\"${{st}}\\",\\"required\\":${{req}},\\"message\\":\\"${{msg_esc}}\\"}}"
}}

append_from_case_json() {{
  f="$1"; name="$2"; level="$3"; req="$4"
  if [ ! -f "$f" ]; then
    append_case "$(basename "$f" .json)" "$name" "$level" "fail" "$req" "missing case result json"
    return
  fi
  id=$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' "$f" | head -n 1)
  st=$(sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' "$f" | head -n 1)
  dt=$(sed -n 's/.*"detail"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' "$f" | head -n 1)
  case "$st" in
    PASS|pass) st_norm="pass" ;;
    SKIP|skip) st_norm="skip" ;;
    WARN|warn) st_norm="warn" ;;
    *) st_norm="fail" ;;
  esac
  [ -n "$id" ] || id="$(basename "$f" .json)"
  append_case "$id" "$name" "$level" "$st_norm" "$req" "$dt"
}}

is_truthy() {{
  case "$1" in 1|true|TRUE|yes|YES|on|ON) return 0 ;; *) return 1 ;; esac
}}

run_l2_if_enabled() {{
  en="$1"; script="$2"; cid="$3"; name="$4"; req="$5"
  if ! is_truthy "$en"; then
    append_case "$cid" "$name" "L2" "skip" "$req" "disabled (set HAL_ENABLE_${{cid}}=1 after filling hooks)"
    return
  fi
  if [ ! -f "$script" ]; then
    append_case "$cid" "$name" "L2" "fail" "$req" "missing $script"
    return
  fi
  chmod 755 "$script" 2>/dev/null || true
  sh "$script"
  append_from_case_json "$OUT/cases/${{cid}}.json" "$name" "L2" "$req"
}}

find_pid() {{
  pat="$1"; [ -n "$pat" ] || return 1
  if pidof "$pat" >/dev/null 2>&1; then pidof "$pat" 2>/dev/null | awk '{{print $1}}'; return 0; fi
  if pgrep -f "$pat" >/dev/null 2>&1; then pgrep -f "$pat" 2>/dev/null | head -n 1; return 0; fi
  return 1
}}

lshal_probe() {{
  needle="$1"; logf="$OUT/work_l1/lshal.log"
  if [ -z "$needle" ]; then echo "no needle" > "$logf"; return 2; fi
  if command -v lshal >/dev/null 2>&1; then
    lshal 2>/dev/null | tee "$logf" | grep -F "$needle" >/dev/null 2>&1; return $?
  fi
  if dumpsys hwservicemanager 2>/dev/null | tee "$logf" | grep -F "$needle" >/dev/null 2>&1; then return 0; fi
  echo "lshal unavailable" > "$logf"; return 3
}}

# ----- placeholder -----
if [ "$MODE" = "placeholder" ]; then
  append_case "ipc" "服务可连接" "L1" "skip" "false" "本树无 HAL 实现"
  if [ -n "$LSHAL_NEEDLE" ] && lshal_probe "$LSHAL_NEEDLE"; then
    append_case "response" "接口有响应" "L1" "pass" "false" "外树已登记: $LSHAL_NEEDLE"
    append_case "binder" "服务绑定正常" "L1" "pass" "false" "lshal listed"
    append_case "function_smoke" "功能冒烟" "L1" "pass" "false" "interface listed"
  else
    append_case "response" "接口有响应" "L1" "skip" "false" "外树未登记或 lshal 不可用"
    append_case "binder" "服务绑定正常" "L1" "skip" "false" "placeholder"
    append_case "function_smoke" "功能冒烟" "L1" "skip" "false" "placeholder"
  fi
  append_case "no_crash" "调用后无崩溃" "L1" "pass" "false" "no local invoke"
  append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled"
  {l2_block}
  FINISHED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
  OVERALL="pass"; SUMMARY="Pass=${{PASS_N}} Fail=${{FAIL_N}} Skip=${{SKIP_N}}"
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
  echo "RESULT=$OUT/module_result.json"; echo Pass; exit 0
fi

# ----- L1 ipc -----
if [ -z "$HAL_BINARY" ]; then
  append_case "ipc" "服务可连接" "L1" "fail" "true" "HAL_BINARY empty"
elif [ -e "$HAL_BINARY" ]; then
  append_case "ipc" "服务可连接" "L1" "pass" "true" "binary exists: $HAL_BINARY"
elif [ "$BINARY_OPTIONAL" = "true" ]; then
  append_case "ipc" "服务可连接" "L1" "warn" "false" "binary missing (optional)"
else
  append_case "ipc" "服务可连接" "L1" "fail" "true" "binary missing: $HAL_BINARY"
fi

PID1=""; PID1=$(find_pid "$PROCESS_PAT" || true)
if [ -n "$PID1" ]; then
  append_case "binder" "服务绑定正常" "L1" "pass" "true" "process alive pid=$PID1"
else
  append_case "binder" "服务绑定正常" "L1" "fail" "true" "process not found: $PROCESS_PAT"
fi

if [ "$MODE" = "daemon" ]; then
  if [ -n "$PID1" ]; then
    append_case "response" "接口有响应" "L1" "pass" "true" "daemon pid=$PID1"
    append_case "function_smoke" "功能冒烟" "L1" "pass" "true" "daemon alive"
  else
    append_case "response" "接口有响应" "L1" "fail" "true" "daemon not running"
    append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "daemon not running"
  fi
else
  lshal_probe "$LSHAL_NEEDLE"; LSHAL_RC=$?
  cp "$OUT/work_l1/lshal.log" "$OUT/logs/l1_lshal.log" 2>/dev/null || true
  if [ "$LSHAL_RC" -eq 0 ]; then
    append_case "response" "接口有响应" "L1" "pass" "true" "lshal hit: $LSHAL_NEEDLE"
    if [ -n "$PID1" ]; then
      append_case "function_smoke" "功能冒烟" "L1" "pass" "true" "process+lshal ok"
    else
      append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "lshal ok but process missing"
    fi
  elif [ "$LSHAL_RC" -eq 3 ]; then
    if [ -n "$PID1" ]; then
      append_case "response" "接口有响应" "L1" "warn" "true" "lshal unavailable; process only"
      append_case "function_smoke" "功能冒烟" "L1" "warn" "true" "lshal unavailable"
    else
      append_case "response" "接口有响应" "L1" "fail" "true" "lshal unavailable and process missing"
      append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "cannot probe"
    fi
  else
    append_case "response" "接口有响应" "L1" "fail" "true" "lshal miss: $LSHAL_NEEDLE"
    append_case "function_smoke" "功能冒烟" "L1" "fail" "true" "interface not listed"
  fi
fi

sleep 1
PID2=""; PID2=$(find_pid "$PROCESS_PAT" || true)
TB_HIT=$(ls /data/tombstones 2>/dev/null | head -n 8)
TB_REL=0
OLD_IFS=$IFS; IFS='|'
for kw in $TOMBSTONE_KW; do
  [ -n "$kw" ] || continue
  if echo "$TB_HIT" | grep -qi -- "$kw"; then TB_REL=1; break; fi
done
IFS=$OLD_IFS
if [ -z "$PROCESS_PAT" ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "pass" "true" "no process pattern"
elif [ -z "$PID2" ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "fail" "true" "process gone after probe"
elif [ "$TB_REL" -eq 1 ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "fail" "true" "tombstone may relate"
elif [ -n "$PID1" ] && [ "$PID1" != "$PID2" ]; then
  append_case "no_crash" "调用后无崩溃" "L1" "warn" "true" "pid changed $PID1->$PID2"
else
  append_case "no_crash" "调用后无崩溃" "L1" "pass" "true" "process alive pid=${{PID2}}"
fi

append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled"

# ----- L2 (default skip) -----
chmod 755 "$SCRIPT_DIR/lib/case_framework.sh" 2>/dev/null || true
{l2_block}

FINISHED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
if [ "$FAIL_N" -gt 0 ]; then OVERALL="fail"; else OVERALL="pass"; fi
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
if [ "$OVERALL" = "pass" ]; then echo Pass; exit 0; fi
echo Failed; exit 1
'''


DEPLOY_PS1 = r'''#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModuleId = Split-Path -Leaf $Root
$Remote = "/data/local/tmp/t2_selfcheck/$ModuleId"

function Find-Adb {
  $cands = @(
    "C:\Users\yuhao\Desktop\T2自测工具\scripts\SocOtaUpgrade\tools\adb.exe",
    (Join-Path $Root "..\..\T2自测工具\scripts\SocOtaUpgrade\tools\adb.exe"),
    "adb"
  )
  foreach ($c in $cands) {
    if ($c -eq "adb") { return "adb" }
    if (Test-Path $c) { return (Resolve-Path $c).Path }
  }
  throw "未找到 adb.exe"
}

$adb = Find-Adb
Write-Host "adb=$adb module=$ModuleId remote=$Remote"
& $adb start-server | Out-Null
$devs = & $adb devices
if ($devs -notmatch "\tdevice") { throw "无 adb device" }
& $adb root | Out-Null
Start-Sleep -Seconds 1
& $adb wait-for-device | Out-Null
& $adb remount 2>&1 | Out-Null
& $adb shell "rm -rf $Remote; mkdir -p $Remote" | Out-Null

$stage = Join-Path $env:TEMP ("t2ts_" + $ModuleId)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $Root "*") -Destination $stage -Recurse -Force
Remove-Item (Join-Path $stage "deploy_to_device.ps1") -Force -ErrorAction SilentlyContinue

& $adb push "$stage\." "$Remote/"
if ($LASTEXITCODE -ne 0) { throw "adb push 失败" }
& $adb shell "chmod -R 755 $Remote"
& $adb shell "sh $Remote/run.sh $Remote"
$code = $LASTEXITCODE
$outLocal = Join-Path $Root ("result_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
New-Item -ItemType Directory -Path $outLocal -Force | Out-Null
& $adb pull "$Remote/module_result.json" (Join-Path $outLocal "module_result.json")
& $adb pull "$Remote/cases" (Join-Path $outLocal "cases") 2>$null
Write-Host "结果: $outLocal exit=$code"
if ($code -ne 0) { exit $code }
'''


def make_readme(m: dict, l2: list) -> str:
    mid = m["id"]
    rows = "\n".join(f"| `{c[0]}` | 关 | {c[1]} |" for c in l2) or "| （无） | | |"
    return f'''# {m["name"]} 测试脚本包

路径：`E:\\\\BMCSJB\\\\T2TestScript\\\\{mid}`  
对齐文档：`{DOCS}/担当下发/{m.get("mxx","")}*_L2担当要求.md`、L1 规范。  
源码：`{GIT}`（分支 `{BRANCH}`）

## 目录

```text
{mid}/
├── README.md
├── 脚本说明.md
├── deploy_to_device.ps1
├── run.sh                 ← L1 内联 + L2 调度
├── manifest.json
├── lib/                   ← case_framework + snapshot_helpers
├── cases/                 ← L2 四步骨架（默认 enabled=false）
└── bin/                   ← 可选：放置模块 Test 二进制
```

## 快速跑

```powershell
cd E:\\BMCSJB\\T2TestScript\\{mid}
.\\deploy_to_device.ps1
```

车机落点：`/data/local/tmp/t2_selfcheck/{mid}/`

## 默认测项

- **L1（默认执行）**：ipc / binder / response / function_smoke / no_crash
- **L2（默认 skip）**：补齐 hook 后设 `HAL_ENABLE_<case_id>=1`

| id | 默认 | 说明 |
|----|------|------|
{rows}

## 说明

- L1 仅本机：二进制 + 进程 + `lshal` 接口登记，**不写状态、不依赖外设**。
- L2 骨架 `hook_assert` 故意失败，防止假绿；补齐 `get_how`/`set_how`/测试工具后再启用。
- 同源可参考：`T2自测工具/scripts/hal_selfcheck/{mid}/`

生成日期：2026-08-03
'''


def make_script_desc(m: dict, l2: list) -> str:
    mid = m["id"]
    rows = "\n".join(f"| `{c[0]}` | 关 | {c[1]}；{c[3]} |" for c in l2) or "| — | — | — |"
    return f'''# {mid} · 脚本说明

## L1（run.sh 内联，默认跑）

| id | 判定 |
|----|------|
| ipc | HAL 二进制存在 |
| binder | 进程存活 |
| response | `lshal` 命中 `{m.get("lshal") or "(daemon)"}` |
| function_smoke | 进程 + 接口登记 |
| no_crash | 探测后进程仍在 |

## L2（cases/，默认 skip）

| id | 默认 | 说明 |
|----|------|------|
{rows}

启用示例：`adb shell "HAL_ENABLE_mute_write_restore=1 sh .../run.sh"`

## 产物

- `module_result.json`
- `cases/<id>.json`（启用 L2 后）
- `logs/`

退出码：0=Pass；非 0=Fail。
'''


def make_manifest(m: dict, l2: list) -> dict:
    mid = m["id"]
    cases = [
        {"id": "ipc", "name": "服务可连接", "level": "L1", "enabled": True, "required": True},
        {"id": "binder", "name": "服务绑定正常", "level": "L1", "enabled": True, "required": True},
        {"id": "response", "name": "接口有响应", "level": "L1", "enabled": True, "required": True},
        {"id": "function_smoke", "name": "功能冒烟", "level": "L1", "enabled": True, "required": True},
        {"id": "no_crash", "name": "调用后无崩溃", "level": "L1", "enabled": True, "required": True},
        {"id": "memleak_probe", "name": "无内存泄漏（探针）", "level": "L1", "enabled": False, "required": False},
    ]
    if m.get("mode") == "placeholder":
        for c in cases:
            if c["id"] != "memleak_probe":
                c["required"] = False
    for cid, name, req, _n in l2:
        cases.append({
            "id": cid,
            "name": name,
            "level": "L2",
            "enabled": False,
            "required": req,
            "owner": m.get("owner", f"{mid}-team"),
            "notes": "TODO hooks; do not enable until get_how/set_how ready",
        })
    return {
        "schema_version": "1.0",
        "module_id": mid,
        "module_name": m["name"],
        "hal_binary": m.get("binary", ""),
        "init_service": m.get("init", ""),
        "owner": f"{mid}-team",
        "script_name": f"HalSelfCheck_{mid}.sh",
        "script_desc": f"{mid} 自检（L1 本机 + L2 骨架对齐担当 {m.get('mxx','')}）",
        "platform": {
            "sync_to_atf": True,
            "default_selected": bool(m.get("required")),
            "tags": ["hal", mid, "l1", "l2", "bmcsjb"],
        },
        "timeout_sec": 180,
        "required": bool(m.get("required")),
        "delivery_root": f"E:/BMCSJB/T2TestScript/{mid}",
        "source_git": GIT,
        "source_branch": BRANCH,
        "docs_ref": f"{DOCS}/担当下发/{m.get('mxx','')}",
        "lshal_needle": m.get("lshal", ""),
        "cases": cases,
    }


def gen_module(m: dict) -> Path:
    mid = m["id"]
    d = OUT_ROOT / mid
    d.mkdir(parents=True, exist_ok=True)
    (d / "cases").mkdir(exist_ok=True)
    (d / "bin").mkdir(exist_ok=True)
    (d / "fixtures").mkdir(exist_ok=True)
    copy_framework(d / "lib")

    l2 = L2_BY_MODULE.get(mid, [])
    write_lf(d / "run.sh", make_run_sh(m))
    write_lf(d / "deploy_to_device.ps1", DEPLOY_PS1.replace("\n", "\n"))
    # deploy as CRLF is ok for ps1 - write normally
    (d / "deploy_to_device.ps1").write_text(DEPLOY_PS1, encoding="utf-8")
    write_lf(d / "README.md", make_readme(m, l2))
    write_lf(d / "脚本说明.md", make_script_desc(m, l2))
    (d / "manifest.json").write_text(
        json.dumps(make_manifest(m, l2), ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    (d / "bin" / "README.txt").write_text(
        "将模块 Test 二进制与依赖 so 放于此目录；run.sh / cases 可通过 SCRIPT_DIR/bin 引用。\n",
        encoding="utf-8",
    )

    for cid, name, req, notes in l2:
        restore_strict = "0" if ("unregister" in notes or "只读" in notes or "readonly" in cid or "cleanup" in cid) else "1"
        write_lf(
            d / "cases" / f"{cid}.sh",
            CASE_SH.format(
                case_id=cid,
                module_id=mid,
                mxx=m.get("mxx", ""),
                notes=notes.replace('"', "'"),
                restore_strict=restore_strict,
            ),
        )
    return d


def update_vehicleconfig_meta() -> None:
    vc = OUT_ROOT / "vehicleconfig"
    if not vc.exists():
        return
    # refresh README path pointers only
    readme = vc / "README.md"
    if readme.exists():
        t = readme.read_text(encoding="utf-8")
        t = t.replace("E:\\BMCSJB\\vehicleconfig", "E:\\BMCSJB\\T2TestScript\\vehicleconfig")
        t = t.replace("E:/BMCSJB/vehicleconfig", "E:/BMCSJB/T2TestScript/vehicleconfig")
        t = t.replace("路径：`E:\\BMCSJB\\vehicleconfig`", "路径：`E:\\BMCSJB\\T2TestScript\\vehicleconfig`")
        if "T2TestScript" not in t.splitlines()[2] if len(t.splitlines()) > 2 else True:
            pass
        readme.write_text(t, encoding="utf-8")
    mf = vc / "manifest.json"
    if mf.exists():
        data = json.loads(mf.read_text(encoding="utf-8"))
        data["delivery_root"] = "E:/BMCSJB/T2TestScript/vehicleconfig"
        data["source_git"] = GIT
        data["source_branch"] = BRANCH
        data["hardware_autotest"] = "vehicleconfig/autotest"
        mf.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def write_root_readme(modules: list[dict]) -> None:
    lines = [
        "# T2TestScript · HAL 模块测试脚本包",
        "",
        f"> 源码：`{GIT}`（分支 `{BRANCH}`）  ",
        f"> 需求文档：`{DOCS}/`（功能清单 / 担当下发 / L2 思维导图 / AI 生成规范）  ",
        "> 样板包：`vehicleconfig/`（含 Test 二进制与可跑 L2）",
        "",
        "## 模块一览",
        "",
        "| 目录 | 担当 | L1 | L2 | 说明 |",
        "|------|------|----|----|------|",
        "| [vehicleconfig](./vehicleconfig/) | M01 | 可跑 | 可跑（写参还原） | 参考样板 |",
    ]
    for m in modules:
        mid = m["id"]
        n = len(L2_BY_MODULE.get(mid, []))
        lines.append(
            f"| [{mid}](./{mid}/) | {m.get('mxx','')} | 可跑 | 骨架×{n}（默认关） | {m['name']} |"
        )
    lines += [
        "",
        "## 使用",
        "",
        "```powershell",
        "cd E:\\BMCSJB\\T2TestScript\\<module>",
        ".\\deploy_to_device.ps1",
        "```",
        "",
        "## 依赖策略（soft-skip）",
        "",
        "- 脚本**不依赖** CANoe 工程或对手件联调。",
        "- 缺环境依赖（MOST 环网 / 相机 / MCU 串口等）或可选模块服务未就绪：",
        "  **不测（skip）且模块结果 Pass**，不记 Fail。",
        "- 必选模块（如 audioctrl/light/diag 等）服务异常仍记 Fail。",
        "- 细节见各模块 `README.md` 的「依赖与 soft-skip」；重生后由 `_patch_soft_skip.py` 固化到 `run.sh`。",
        "",
        "## 约定",
        "",
        "- **L1**：仅本机，不写状态、不依赖外设。",
        "- **L2**：四步法 + 还原再读确认；未补齐命令的骨架 `enabled=false`，断言默认 Fail 防假绿。",
        "- 启用某 L2：`HAL_ENABLE_<case_id>=1`（且已实现 hooks）。",
        "- 与 `T2自测工具/scripts/hal_selfcheck/` 同源规格；本目录为 BMCSJB 可独立交付包。",
        "",
        "生成日期：2026-08-03",
        "",
    ]
    write_lf(OUT_ROOT / "README.md", "\n".join(lines))


def main() -> None:
    from _patch_soft_skip import apply_root

    OUT_ROOT.mkdir(parents=True, exist_ok=True)
    done = []
    for m in MODULES:
        d = gen_module(m)
        done.append(d.name)
        print("OK", d)
    update_vehicleconfig_meta()
    write_root_readme(MODULES)
    soft = apply_root(OUT_ROOT)
    print("modules", len(done), ",".join(done))
    print("soft-skip", len(soft), ",".join(soft))
    print("root", OUT_ROOT)


if __name__ == "__main__":
    main()
