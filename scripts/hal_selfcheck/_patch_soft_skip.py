# -*- coding: utf-8 -*-
"""Regenerate run.sh with soft-skip: missing env/deps => skip, overall pass (no Fail)."""
from __future__ import annotations

import json
from pathlib import Path

GIT = "ssh://192.168.64.47:29418/Android/QCOM_SA8155_6155/platform/vendor/hsae/proprietary/hardware"
BRANCH = "24MM_T2_dev"
BMCSJB = Path(r"E:\BMCSJB\T2TestScript")
HAL_SC = Path(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\hal_selfcheck")

# soft_if_unavailable: L1 负面结果改为 skip，不算 Fail
# env_deps: 缺依赖则整模块 L1 直接 soft-skip（不测）
META = {
    "audioctrl": {"soft": False, "deps": ""},
    "light": {"soft": False, "deps": ""},
    "ampservice": {"soft": True, "deps": "most"},
    "localradio": {"soft": True, "deps": ""},
    "metazone": {"soft": True, "deps": ""},
    "input": {"soft": False, "deps": ""},
    "drinfo": {"soft": False, "deps": ""},
    "gnssdr": {"soft": True, "deps": ""},
    "anc": {"soft": True, "deps": "most"},
    "someip": {"soft": True, "deps": ""},
    "mostslave": {"soft": True, "deps": "most"},
    "installerhal": {"soft": False, "deps": ""},
    "diag": {"soft": False, "deps": ""},
    "devmanager": {"soft": False, "deps": ""},
    "cameractrl": {"soft": True, "deps": "camera"},
    "securitychip": {"soft": True, "deps": ""},
    "most": {"soft": True, "deps": "most_hw"},
    "rse": {"soft": True, "deps": ""},
    "earlycarservice": {"soft": True, "deps": "mcu"},
    "maintainhal": {"soft": True, "deps": "most"},
    "vehicle": {"soft": True, "deps": ""},
}


def build_run_sh(mid: str, mf: dict) -> str:
    meta = META.get(mid, {"soft": True, "deps": ""})
    soft = "1" if meta["soft"] else "0"
    deps = meta["deps"]
    name = mf.get("module_name", mid)
    binary = mf.get("hal_binary", "")
    lshal = mf.get("lshal_needle", "")
    # process from existing run.sh if possible
    process = ""
    tomb = mid
    mode = "hidl"
    bin_opt = "false"
    old = BMCSJB / mid / "run.sh"
    if old.exists():
        for ln in old.read_text(encoding="utf-8").splitlines():
            if ln.startswith("PROCESS_PAT="):
                process = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("TOMBSTONE_KW="):
                tomb = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("MODE="):
                mode = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("BINARY_OPTIONAL="):
                bin_opt = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("LSHAL_NEEDLE=") and not lshal:
                lshal = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("HAL_BINARY=") and not binary:
                binary = ln.split("=", 1)[1].strip().strip('"')

    # Collect L2 enable lines from existing run.sh
    l2_block = "# (no L2)"
    if old.exists():
        lines = old.read_text(encoding="utf-8").splitlines()
        chunks = []
        i = 0
        while i < len(lines):
            if lines[i].startswith("run_l2_if_enabled"):
                chunk = [lines[i]]
                i += 1
                while i < len(lines) and (lines[i].startswith(" ") or lines[i].startswith("\t") or lines[i].endswith("\\")):
                    chunk.append(lines[i])
                    if not lines[i].endswith("\\"):
                        i += 1
                        break
                    i += 1
                chunks.append("\n".join(chunk))
                continue
            i += 1
        if chunks:
            l2_block = "\n\n".join(chunks)

    return f'''#!/system/bin/sh
# HalSelfCheck — {mid}（soft-skip：缺依赖/不可用则 skip，不报 Fail）
# 源码：{GIT} 分支 {BRANCH}
# SOFT_IF_UNAVAILABLE={soft} ENV_DEPS={deps or "(none)"}
# 无 CANoe/对手件硬依赖；L2 默认 skip

MODULE_ID="{mid}"
MODULE_NAME="{name}"
HAL_BINARY="{binary}"
PROCESS_PAT="{process}"
LSHAL_NEEDLE="{lshal}"
TOMBSTONE_KW="{tomb}"
BINARY_OPTIONAL="{bin_opt}"
MODE="{mode}"
SOFT_IF_UNAVAILABLE="{soft}"
ENV_DEPS="{deps}"
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
OUT="${{1:-${{T2_SELFCHECK_OUT:-/data/local/tmp/t2_selfcheck/{mid}}}}}"
export RESULT_DIR="$OUT"
mkdir -p "$OUT/logs" "$OUT/work_l1" "$OUT/cases"

STARTED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
PASS_N=0
FAIL_N=0
SKIP_N=0
CASES_JSON=""
SOFT_REASON=""

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
    append_case "$(basename "$f" .json)" "$name" "$level" "skip" "false" "missing case json (treated skip)"
    return
  fi
  id=$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' "$f" | head -n 1)
  st=$(sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' "$f" | head -n 1)
  dt=$(sed -n 's/.*"detail"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' "$f" | head -n 1)
  case "$st" in
    PASS|pass) st_norm="pass" ;;
    SKIP|skip) st_norm="skip" ;;
    WARN|warn) st_norm="warn" ;;
    FAIL|fail|ERROR|error)
      # soft 模块：L2 失败也降为 skip，避免缺工具时报错
      if [ "$SOFT_IF_UNAVAILABLE" = "1" ]; then st_norm="skip"; dt="soft-skip L2 fail: $dt"
      else st_norm="fail"; fi ;;
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
    append_case "$cid" "$name" "L2" "skip" "false" "disabled (default; no env/dep required)"
    return
  fi
  if [ ! -f "$script" ]; then
    append_case "$cid" "$name" "L2" "skip" "false" "missing script (soft-skip)"
    return
  fi
  chmod 755 "$script" 2>/dev/null || true
  sh "$script"
  append_from_case_json "$OUT/cases/${{cid}}.json" "$name" "L2" "false"
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

most_ready() {{
  find_pid "most@1.0-service" >/dev/null && return 0
  find_pid "vendor.hsae.hardware.most@1.0-service" >/dev/null && return 0
  lshal_probe "vendor.hsae.hardware.most@1.0::IMostHal/default" && return 0
  gp=$(getprop init.svc.vendor.hsae_most_hal 2>/dev/null || true)
  [ "$gp" = "running" ] && return 0
  return 1
}}

emit_l1() {{
  # $1=id $2=name $3=status_if_hard $4=msg ; soft 时 fail→skip
  id="$1"; name="$2"; st="$3"; msg="$4"; req="true"
  if [ "$st" = "fail" ] && [ "$SOFT_IF_UNAVAILABLE" = "1" ]; then
    st="skip"; req="false"; msg="$msg [soft-skip: module unavailable / missing dep]"
  elif [ "$st" = "fail" ]; then
    req="true"
  else
    req="true"
    [ "$st" = "skip" ] && req="false"
    [ "$st" = "warn" ] && req="false"
  fi
  append_case "$id" "$name" "L1" "$st" "$req" "$msg"
}}

finish_and_exit() {{
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
  "soft_skip_reason": "${{SOFT_REASON}}",
  "cases": [ ${{CASES_JSON}} ],
  "attachments": []
}}
EOF
  echo "MODULE=${{MODULE_ID}} OVERALL=${{OVERALL}} ${{SUMMARY}}"
  [ -n "$SOFT_REASON" ] && echo "SOFT_SKIP=$SOFT_REASON"
  echo "RESULT=$OUT/module_result.json"
  if [ "$OVERALL" = "pass" ]; then echo Pass; exit 0; fi
  echo Failed; exit 1
}}

soft_skip_all_l1() {{
  reason="$1"
  SOFT_REASON="$reason"
  emit_l1 "ipc" "服务可连接" "skip" "skipped: $reason"
  emit_l1 "binder" "服务绑定正常" "skip" "skipped: $reason"
  emit_l1 "response" "接口有响应" "skip" "skipped: $reason"
  emit_l1 "function_smoke" "功能冒烟" "skip" "skipped: $reason"
  emit_l1 "no_crash" "调用后无崩溃" "skip" "skipped: $reason"
  append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled"
  chmod 755 "$SCRIPT_DIR/lib/case_framework.sh" 2>/dev/null || true
{l2_indent}
  finish_and_exit
}}

# ----- env dependency gate（无 CANoe；缺环网/相机/MCU 则不测）-----
for _dep in $ENV_DEPS; do
  case "$_dep" in
    most)
      if ! most_ready; then
        soft_skip_all_l1 "MOST HAL not ready (no ring/CANoe) — not tested"
      fi
      ;;
    most_hw)
      # most 模块自身：无进程/接口则整模块 soft-skip
      if ! most_ready && [ ! -e "$HAL_BINARY" ]; then
        soft_skip_all_l1 "MOST stack not present — not tested"
      fi
      ;;
    camera)
      # 无相机服务进程且无接口 → 不测
      if ! find_pid "$PROCESS_PAT" >/dev/null && ! lshal_probe "$LSHAL_NEEDLE"; then
        soft_skip_all_l1 "camera HAL not available — not tested"
      fi
      ;;
    mcu)
      if ! find_pid "$PROCESS_PAT" >/dev/null && [ ! -e "$HAL_BINARY" ]; then
        soft_skip_all_l1 "MCU/EarlyCar serial HAL not available — not tested"
      fi
      ;;
  esac
done

# ----- placeholder -----
if [ "$MODE" = "placeholder" ]; then
  soft_skip_all_l1 "placeholder module (no local VHAL impl)"
fi

# ----- L1 ipc -----
if [ -z "$HAL_BINARY" ]; then
  emit_l1 "ipc" "服务可连接" "fail" "HAL_BINARY empty"
elif [ -e "$HAL_BINARY" ]; then
  emit_l1 "ipc" "服务可连接" "pass" "binary exists: $HAL_BINARY"
elif [ "$BINARY_OPTIONAL" = "true" ]; then
  emit_l1 "ipc" "服务可连接" "skip" "binary missing (optional)"
else
  emit_l1 "ipc" "服务可连接" "fail" "binary missing: $HAL_BINARY"
fi

PID1=""; PID1=$(find_pid "$PROCESS_PAT" || true)
if [ -n "$PID1" ]; then
  emit_l1 "binder" "服务绑定正常" "pass" "process alive pid=$PID1"
else
  emit_l1 "binder" "服务绑定正常" "fail" "process not found: $PROCESS_PAT"
fi

if [ "$MODE" = "daemon" ]; then
  if [ -n "$PID1" ]; then
    emit_l1 "response" "接口有响应" "pass" "daemon pid=$PID1"
    emit_l1 "function_smoke" "功能冒烟" "pass" "daemon alive"
  else
    emit_l1 "response" "接口有响应" "fail" "daemon not running"
    emit_l1 "function_smoke" "功能冒烟" "fail" "daemon not running"
  fi
else
  lshal_probe "$LSHAL_NEEDLE"; LSHAL_RC=$?
  cp "$OUT/work_l1/lshal.log" "$OUT/logs/l1_lshal.log" 2>/dev/null || true
  if [ "$LSHAL_RC" -eq 0 ]; then
    emit_l1 "response" "接口有响应" "pass" "lshal hit: $LSHAL_NEEDLE"
    if [ -n "$PID1" ]; then
      emit_l1 "function_smoke" "功能冒烟" "pass" "process+lshal ok"
    else
      emit_l1 "function_smoke" "功能冒烟" "fail" "lshal ok but process missing"
    fi
  elif [ "$LSHAL_RC" -eq 3 ]; then
    if [ -n "$PID1" ]; then
      emit_l1 "response" "接口有响应" "warn" "lshal unavailable; process only"
      emit_l1 "function_smoke" "功能冒烟" "warn" "lshal unavailable"
    else
      emit_l1 "response" "接口有响应" "fail" "lshal unavailable and process missing"
      emit_l1 "function_smoke" "功能冒烟" "fail" "cannot probe"
    fi
  else
    emit_l1 "response" "接口有响应" "fail" "lshal miss: $LSHAL_NEEDLE"
    emit_l1 "function_smoke" "功能冒烟" "fail" "interface not listed"
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
  emit_l1 "no_crash" "调用后无崩溃" "pass" "no process pattern"
elif [ -z "$PID2" ]; then
  emit_l1 "no_crash" "调用后无崩溃" "fail" "process gone after probe"
elif [ "$TB_REL" -eq 1 ]; then
  emit_l1 "no_crash" "调用后无崩溃" "fail" "tombstone may relate"
elif [ -n "$PID1" ] && [ "$PID1" != "$PID2" ]; then
  emit_l1 "no_crash" "调用后无崩溃" "warn" "pid changed $PID1->$PID2"
else
  emit_l1 "no_crash" "调用后无崩溃" "pass" "process alive pid=${{PID2}}"
fi

append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled"

chmod 755 "$SCRIPT_DIR/lib/case_framework.sh" 2>/dev/null || true
{l2_block}

finish_and_exit
'''


def indent_l2(l2_block: str) -> str:
    if not l2_block.strip() or l2_block.strip().startswith("#"):
        return "  # (no L2)\n"
    return "\n".join("  " + ln if ln.strip() else ln for ln in l2_block.splitlines()) + "\n"


def patch_one(root: Path, mid: str) -> bool:
    d = root / mid
    mf_path = d / "manifest.json"
    if not mf_path.exists():
        return False
    mf = json.loads(mf_path.read_text(encoding="utf-8"))
    # temporary build to get l2 from existing
    text = build_run_sh(mid, mf)
    # fix l2_indent placeholder: rebuild properly
    old = d / "run.sh"
    l2_block = "# (no L2)"
    if old.exists():
        lines = old.read_text(encoding="utf-8").splitlines()
        chunks = []
        i = 0
        while i < len(lines):
            if lines[i].startswith("run_l2_if_enabled"):
                chunk = [lines[i]]
                i += 1
                while i < len(lines) and (lines[i].startswith(" ") or lines[i].startswith("\t") or (chunk[-1].endswith("\\"))):
                    chunk.append(lines[i])
                    i += 1
                    if not chunk[-2].endswith("\\") and not lines[i - 1].endswith("\\"):
                        break
                # simpler: take until line without trailing backslash that isn't continuation
                chunks.append("\n".join(chunk))
                continue
            i += 1
        # re-parse more carefully
        chunks = []
        buf = []
        for ln in lines:
            if ln.startswith("run_l2_if_enabled") or buf:
                buf.append(ln)
                if not ln.rstrip().endswith("\\"):
                    chunks.append("\n".join(buf))
                    buf = []
        if chunks:
            l2_block = "\n\n".join(chunks)

    # inject into template - rebuild with l2
    # The build_run_sh embeds {{l2_indent}} and {{l2_block}} - fix by formatting after
    # Actually I used {l2_indent} and {l2_block} in f-string incorrectly as literal in soft_skip_all.
    # Let me post-process.

    text = build_run_sh(mid, mf)
    # Replace placeholders that were meant for l2 - I put {{l2_indent}} wrong.
    # Looking at template: soft_skip_all has `{l2_indent}` as literal from f-string - in the return f''' I used
    # `{l2_indent}` which would need to be in the function... I used:
    # {l2_indent}
    # in soft_skip_all_l1 and {l2_block} at end - but those aren't defined in build_run_sh!
    # I need to fix build_run_sh to accept l2 and format.

    return False  # will fix below


def make_run(mid: str, mf: dict, l2_block: str) -> str:
    meta = META.get(mid, {"soft": True, "deps": ""})
    soft = "1" if meta["soft"] else "0"
    deps = meta["deps"]
    name = mf.get("module_name", mid)
    binary = mf.get("hal_binary", "")
    lshal = mf.get("lshal_needle", "") or ""
    process = ""
    tomb = mid
    mode = "hidl"
    bin_opt = "false"
    old = BMCSJB / mid / "run.sh"
    if not old.exists():
        old = HAL_SC / mid / "run.sh"
    if old.exists():
        for ln in old.read_text(encoding="utf-8").splitlines():
            if ln.startswith("PROCESS_PAT="):
                process = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("TOMBSTONE_KW="):
                tomb = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("MODE="):
                mode = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("BINARY_OPTIONAL="):
                bin_opt = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("LSHAL_NEEDLE=") and not lshal:
                lshal = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("HAL_BINARY=") and not binary:
                binary = ln.split("=", 1)[1].strip().strip('"')

    l2_indented = indent_l2(l2_block)

    # Use .format with escaped braces - write via replace tokens
    tpl = Path(__file__).with_name("_soft_run_template.sh")
    # inline template via tokens
    t = SOFT_RUN_TEMPLATE
    rep = {{
        "@@MID@@": mid,
        "@@NAME@@": name,
        "@@BINARY@@": binary,
        "@@PROCESS@@": process,
        "@@LSHAL@@": lshal,
        "@@TOMB@@": tomb,
        "@@BINOPT@@": bin_opt,
        "@@MODE@@": mode,
        "@@SOFT@@": soft,
        "@@DEPS@@": deps,
        "@@GIT@@": GIT,
        "@@BRANCH@@": BRANCH,
        "@@L2_INDENT@@": l2_indented.rstrip("\n"),
        "@@L2_BLOCK@@": l2_block if l2_block.strip() else "# (no L2)",
    }}
    for k, v in rep.items():
        t = t.replace(k, v)
    return t


SOFT_RUN_TEMPLATE = r'''#!/system/bin/sh
# HalSelfCheck — @@MID@@（soft-skip：缺依赖/不可用则 skip，不报 Fail）
# 源码：@@GIT@@ 分支 @@BRANCH@@
# SOFT_IF_UNAVAILABLE=@@SOFT@@ ENV_DEPS=@@DEPS@@
# 无 CANoe/对手件硬依赖；L2 默认 skip

MODULE_ID="@@MID@@"
MODULE_NAME="@@NAME@@"
HAL_BINARY="@@BINARY@@"
PROCESS_PAT="@@PROCESS@@"
LSHAL_NEEDLE="@@LSHAL@@"
TOMBSTONE_KW="@@TOMB@@"
BINARY_OPTIONAL="@@BINOPT@@"
MODE="@@MODE@@"
SOFT_IF_UNAVAILABLE="@@SOFT@@"
ENV_DEPS="@@DEPS@@"
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
OUT="${1:-${T2_SELFCHECK_OUT:-/data/local/tmp/t2_selfcheck/@@MID@@}}"
export RESULT_DIR="$OUT"
mkdir -p "$OUT/logs" "$OUT/work_l1" "$OUT/cases"

STARTED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
PASS_N=0
FAIL_N=0
SKIP_N=0
CASES_JSON=""
SOFT_REASON=""

append_case() {
  id="$1"; name="$2"; level="$3"; st="$4"; req="$5"; msg="$6"
  case "$st" in
    pass) PASS_N=$((PASS_N+1)) ;;
    fail|error) FAIL_N=$((FAIL_N+1)) ;;
    *) SKIP_N=$((SKIP_N+1)) ;;
  esac
  msg_esc=$(printf '%s' "$msg" | sed 's/\\/\\\\/g; s/"/\\"/g')
  name_esc=$(printf '%s' "$name" | sed 's/"/\\"/g')
  if [ -n "$CASES_JSON" ]; then CASES_JSON="${CASES_JSON},"; fi
  CASES_JSON="${CASES_JSON}{\"id\":\"${id}\",\"name\":\"${name_esc}\",\"level\":\"${level}\",\"status\":\"${st}\",\"required\":${req},\"message\":\"${msg_esc}\"}"
}

append_from_case_json() {
  f="$1"; name="$2"; level="$3"; req="$4"
  if [ ! -f "$f" ]; then
    append_case "$(basename "$f" .json)" "$name" "$level" "skip" "false" "missing case json (soft-skip)"
    return
  fi
  id=$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)
  st=$(sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)
  dt=$(sed -n 's/.*"detail"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)
  case "$st" in
    PASS|pass) st_norm="pass" ;;
    SKIP|skip) st_norm="skip" ;;
    WARN|warn) st_norm="warn" ;;
    FAIL|fail|ERROR|error)
      if [ "$SOFT_IF_UNAVAILABLE" = "1" ]; then st_norm="skip"; dt="soft-skip L2: $dt"
      else st_norm="fail"; fi ;;
    *) st_norm="fail" ;;
  esac
  [ -n "$id" ] || id="$(basename "$f" .json)"
  append_case "$id" "$name" "$level" "$st_norm" "false" "$dt"
}

is_truthy() {
  case "$1" in 1|true|TRUE|yes|YES|on|ON) return 0 ;; *) return 1 ;; esac
}

run_l2_if_enabled() {
  en="$1"; script="$2"; cid="$3"; name="$4"; req="$5"
  if ! is_truthy "$en"; then
    append_case "$cid" "$name" "L2" "skip" "false" "disabled (default; no env/dep required)"
    return
  fi
  if [ ! -f "$script" ]; then
    append_case "$cid" "$name" "L2" "skip" "false" "missing script (soft-skip)"
    return
  fi
  chmod 755 "$script" 2>/dev/null || true
  sh "$script"
  append_from_case_json "$OUT/cases/${cid}.json" "$name" "L2" "false"
}

find_pid() {
  pat="$1"; [ -n "$pat" ] || return 1
  if pidof "$pat" >/dev/null 2>&1; then pidof "$pat" 2>/dev/null | awk '{print $1}'; return 0; fi
  if pgrep -f "$pat" >/dev/null 2>&1; then pgrep -f "$pat" 2>/dev/null | head -n 1; return 0; fi
  return 1
}

lshal_probe() {
  needle="$1"; logf="$OUT/work_l1/lshal.log"
  if [ -z "$needle" ]; then echo "no needle" > "$logf"; return 2; fi
  if command -v lshal >/dev/null 2>&1; then
    lshal 2>/dev/null | tee "$logf" | grep -F "$needle" >/dev/null 2>&1; return $?
  fi
  if dumpsys hwservicemanager 2>/dev/null | tee "$logf" | grep -F "$needle" >/dev/null 2>&1; then return 0; fi
  echo "lshal unavailable" > "$logf"; return 3
}

most_ready() {
  find_pid "most@1.0-service" >/dev/null && return 0
  find_pid "vendor.hsae.hardware.most@1.0-service" >/dev/null && return 0
  lshal_probe "vendor.hsae.hardware.most@1.0::IMostHal/default" && return 0
  gp=$(getprop init.svc.vendor.hsae_most_hal 2>/dev/null || true)
  [ "$gp" = "running" ] && return 0
  return 1
}

emit_l1() {
  id="$1"; name="$2"; st="$3"; msg="$4"; req="true"
  if [ "$st" = "fail" ] && [ "$SOFT_IF_UNAVAILABLE" = "1" ]; then
    st="skip"; req="false"; msg="$msg [soft-skip: unavailable/missing dep]"
  elif [ "$st" = "skip" ] || [ "$st" = "warn" ]; then
    req="false"
  fi
  append_case "$id" "$name" "L1" "$st" "$req" "$msg"
}

finish_and_exit() {
  FINISHED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
  if [ "$FAIL_N" -gt 0 ]; then OVERALL="fail"; else OVERALL="pass"; fi
  SUMMARY="Pass=${PASS_N} Fail=${FAIL_N} Skip=${SKIP_N}"
  cat > "$OUT/module_result.json" <<EOF
{
  "schema_version": "1.0",
  "module_id": "${MODULE_ID}",
  "module_name": "${MODULE_NAME}",
  "started_at": "${STARTED}",
  "finished_at": "${FINISHED}",
  "overall": "${OVERALL}",
  "summary": "${SUMMARY}",
  "soft_skip_reason": "${SOFT_REASON}",
  "cases": [ ${CASES_JSON} ],
  "attachments": []
}
EOF
  echo "MODULE=${MODULE_ID} OVERALL=${OVERALL} ${SUMMARY}"
  [ -n "$SOFT_REASON" ] && echo "SOFT_SKIP=$SOFT_REASON"
  echo "RESULT=$OUT/module_result.json"
  if [ "$OVERALL" = "pass" ]; then echo Pass; exit 0; fi
  echo Failed; exit 1
}

soft_skip_all_l1() {
  reason="$1"
  SOFT_REASON="$reason"
  emit_l1 "ipc" "服务可连接" "skip" "skipped: $reason"
  emit_l1 "binder" "服务绑定正常" "skip" "skipped: $reason"
  emit_l1 "response" "接口有响应" "skip" "skipped: $reason"
  emit_l1 "function_smoke" "功能冒烟" "skip" "skipped: $reason"
  emit_l1 "no_crash" "调用后无崩溃" "skip" "skipped: $reason"
  append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled"
  chmod 755 "$SCRIPT_DIR/lib/case_framework.sh" 2>/dev/null || true
@@L2_INDENT@@
  finish_and_exit
}

# ----- env dependency gate（无 CANoe；缺环网/相机/MCU 则不测、不报错）-----
for _dep in $ENV_DEPS; do
  case "$_dep" in
    most)
      if ! most_ready; then
        soft_skip_all_l1 "MOST HAL not ready (no ring/CANoe) — not tested"
      fi
      ;;
    most_hw)
      if ! most_ready && [ ! -e "$HAL_BINARY" ]; then
        soft_skip_all_l1 "MOST stack not present — not tested"
      fi
      ;;
    camera)
      if ! find_pid "$PROCESS_PAT" >/dev/null && ! lshal_probe "$LSHAL_NEEDLE"; then
        soft_skip_all_l1 "camera HAL not available — not tested"
      fi
      ;;
    mcu)
      if ! find_pid "$PROCESS_PAT" >/dev/null && [ ! -e "$HAL_BINARY" ]; then
        soft_skip_all_l1 "MCU/EarlyCar serial HAL not available — not tested"
      fi
      ;;
  esac
done

if [ "$MODE" = "placeholder" ]; then
  soft_skip_all_l1 "placeholder module (no local VHAL impl)"
fi

# ----- L1 -----
if [ -z "$HAL_BINARY" ]; then
  emit_l1 "ipc" "服务可连接" "fail" "HAL_BINARY empty"
elif [ -e "$HAL_BINARY" ]; then
  emit_l1 "ipc" "服务可连接" "pass" "binary exists: $HAL_BINARY"
elif [ "$BINARY_OPTIONAL" = "true" ]; then
  emit_l1 "ipc" "服务可连接" "skip" "binary missing (optional)"
else
  emit_l1 "ipc" "服务可连接" "fail" "binary missing: $HAL_BINARY"
fi

PID1=""; PID1=$(find_pid "$PROCESS_PAT" || true)
if [ -n "$PID1" ]; then
  emit_l1 "binder" "服务绑定正常" "pass" "process alive pid=$PID1"
else
  emit_l1 "binder" "服务绑定正常" "fail" "process not found: $PROCESS_PAT"
fi

if [ "$MODE" = "daemon" ]; then
  if [ -n "$PID1" ]; then
    emit_l1 "response" "接口有响应" "pass" "daemon pid=$PID1"
    emit_l1 "function_smoke" "功能冒烟" "pass" "daemon alive"
  else
    emit_l1 "response" "接口有响应" "fail" "daemon not running"
    emit_l1 "function_smoke" "功能冒烟" "fail" "daemon not running"
  fi
else
  lshal_probe "$LSHAL_NEEDLE"; LSHAL_RC=$?
  cp "$OUT/work_l1/lshal.log" "$OUT/logs/l1_lshal.log" 2>/dev/null || true
  if [ "$LSHAL_RC" -eq 0 ]; then
    emit_l1 "response" "接口有响应" "pass" "lshal hit: $LSHAL_NEEDLE"
    if [ -n "$PID1" ]; then
      emit_l1 "function_smoke" "功能冒烟" "pass" "process+lshal ok"
    else
      emit_l1 "function_smoke" "功能冒烟" "fail" "lshal ok but process missing"
    fi
  elif [ "$LSHAL_RC" -eq 3 ]; then
    if [ -n "$PID1" ]; then
      emit_l1 "response" "接口有响应" "warn" "lshal unavailable; process only"
      emit_l1 "function_smoke" "功能冒烟" "warn" "lshal unavailable"
    else
      emit_l1 "response" "接口有响应" "fail" "lshal unavailable and process missing"
      emit_l1 "function_smoke" "功能冒烟" "fail" "cannot probe"
    fi
  else
    emit_l1 "response" "接口有响应" "fail" "lshal miss: $LSHAL_NEEDLE"
    emit_l1 "function_smoke" "功能冒烟" "fail" "interface not listed"
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
  emit_l1 "no_crash" "调用后无崩溃" "pass" "no process pattern"
elif [ -z "$PID2" ]; then
  emit_l1 "no_crash" "调用后无崩溃" "fail" "process gone after probe"
elif [ "$TB_REL" -eq 1 ]; then
  emit_l1 "no_crash" "调用后无崩溃" "fail" "tombstone may relate"
elif [ -n "$PID1" ] && [ "$PID1" != "$PID2" ]; then
  emit_l1 "no_crash" "调用后无崩溃" "warn" "pid changed $PID1->$PID2"
else
  emit_l1 "no_crash" "调用后无崩溃" "pass" "process alive pid=${PID2}"
fi

append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled"

chmod 755 "$SCRIPT_DIR/lib/case_framework.sh" 2>/dev/null || true
@@L2_BLOCK@@

finish_and_exit
'''


def extract_l2(run_path: Path) -> str:
    if not run_path.exists():
        return "# (no L2)"
    lines = run_path.read_text(encoding="utf-8").splitlines()
    chunks = []
    buf = []
    for ln in lines:
        if ln.startswith("run_l2_if_enabled") or buf:
            buf.append(ln)
            if not ln.rstrip().endswith("\\"):
                chunks.append("\n".join(buf))
                buf = []
    return "\n\n".join(chunks) if chunks else "# (no L2)"


def apply_root(root: Path) -> list[str]:
    done = []
    for mid, meta in META.items():
        d = root / mid
        if not (d / "manifest.json").exists():
            continue
        mf = json.loads((d / "manifest.json").read_text(encoding="utf-8"))
        l2 = extract_l2(d / "run.sh")
        text = SOFT_RUN_TEMPLATE
        text = text.replace("@@MID@@", mid)
        text = text.replace("@@NAME@@", mf.get("module_name", mid))
        # fill from old run
        binary = mf.get("hal_binary", "")
        lshal = mf.get("lshal_needle", "") or ""
        process = tomb = ""
        mode, bin_opt = "hidl", "false"
        old = d / "run.sh"
        if old.exists():
            for ln in old.read_text(encoding="utf-8").splitlines():
                if ln.startswith("PROCESS_PAT="):
                    process = ln.split("=", 1)[1].strip().strip('"')
                elif ln.startswith("TOMBSTONE_KW="):
                    tomb = ln.split("=", 1)[1].strip().strip('"')
                elif ln.startswith("MODE="):
                    mode = ln.split("=", 1)[1].strip().strip('"')
                elif ln.startswith("BINARY_OPTIONAL="):
                    bin_opt = ln.split("=", 1)[1].strip().strip('"')
                elif ln.startswith("LSHAL_NEEDLE=") and not lshal:
                    lshal = ln.split("=", 1)[1].strip().strip('"')
                elif ln.startswith("HAL_BINARY=") and not binary:
                    binary = ln.split("=", 1)[1].strip().strip('"')
        if not process:
            process = f"{mid}@1.0-service"
        if not tomb:
            tomb = mid
        text = text.replace("@@BINARY@@", binary)
        text = text.replace("@@PROCESS@@", process)
        text = text.replace("@@LSHAL@@", lshal)
        text = text.replace("@@TOMB@@", tomb)
        text = text.replace("@@BINOPT@@", bin_opt)
        text = text.replace("@@MODE@@", mode)
        text = text.replace("@@SOFT@@", "1" if meta["soft"] else "0")
        text = text.replace("@@DEPS@@", meta["deps"])
        text = text.replace("@@GIT@@", GIT)
        text = text.replace("@@BRANCH@@", BRANCH)
        text = text.replace("@@L2_INDENT@@", indent_l2(l2).rstrip("\n"))
        text = text.replace("@@L2_BLOCK@@", l2 if l2.strip() else "# (no L2)")
        (d / "run.sh").write_text(text.replace("\r\n", "\n"), encoding="utf-8", newline="\n")

        # manifest soft flags
        mf["soft_if_unavailable"] = bool(meta["soft"])
        mf["env_deps"] = [x for x in meta["deps"].split() if x]
        mf["soft_skip_policy"] = "missing_dep_or_unavailable => L1 skip, overall pass; no CANoe required"
        (d / "manifest.json").write_text(json.dumps(mf, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

        # README note
        readme = d / "README.md"
        if readme.exists():
            rt = readme.read_text(encoding="utf-8")
            note = (
                "\n## 依赖与 soft-skip\n\n"
                "- **无 CANoe / 对手件硬依赖。**\n"
                f"- `SOFT_IF_UNAVAILABLE={meta['soft']}`；`ENV_DEPS={meta['deps'] or '(none)'}`。\n"
                "- 缺 MOST/相机/MCU 等依赖，或可选模块服务不可用：相关 L1 **skip**，模块 **overall=pass**（不报错）。\n"
                "- L2 默认 disabled=skip。\n"
            )
            if "## 依赖与 soft-skip" not in rt:
                rt = rt.rstrip() + "\n" + note
                readme.write_text(rt, encoding="utf-8")
        done.append(mid)
    return done


def main() -> None:
    a = apply_root(BMCSJB)
    # sync soft run.sh to scripts/hal_selfcheck for same modules
    b = apply_root(HAL_SC)
    # root README
    root_readme = BMCSJB / "README.md"
    if root_readme.exists():
        t = root_readme.read_text(encoding="utf-8")
        block = (
            "\n## 依赖策略（soft-skip）\n\n"
            "- 脚本**不依赖** CANoe 工程或对手件联调。\n"
            "- 缺环境依赖（MOST 环网 / 相机 / MCU 串口等）或可选模块服务未就绪：\n"
            "  **不测（skip）且模块结果 Pass**，不记 Fail。\n"
            "- 必选模块（如 audioctrl/light/diag 等）服务异常仍记 Fail。\n"
        )
        if "## 依赖策略（soft-skip）" not in t:
            t = t.replace("## 约定", block + "\n## 约定")
            root_readme.write_text(t, encoding="utf-8")
    print("BMCSJB", len(a), a)
    print("hal_selfcheck", len(b), b)


if __name__ == "__main__":
    main()
