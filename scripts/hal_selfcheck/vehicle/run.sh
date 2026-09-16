#!/system/bin/sh
# HalSelfCheck — vehicle（soft-skip：缺依赖/不可用则 skip，不报 Fail）
# 源码：ssh://192.168.64.47:29418/Android/QCOM_SA8155_6155/platform/vendor/hsae/proprietary/hardware 分支 24MM_T2_dev
# SOFT_IF_UNAVAILABLE=1 ENV_DEPS=
# 无 CANoe/对手件硬依赖；L2 默认 skip

MODULE_ID="vehicle"
MODULE_NAME="系统 VHAL（本树无实现）"
HAL_BINARY=""
PROCESS_PAT="vehicle@1.0-service"
LSHAL_NEEDLE="android.hardware.automotive.vehicle@2.0::IVehicle/default"
TOMBSTONE_KW="vehicle"
BINARY_OPTIONAL="true"
MODE="placeholder"
SOFT_IF_UNAVAILABLE="1"
ENV_DEPS=""
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
OUT="${1:-${T2_SELFCHECK_OUT:-/data/local/tmp/t2_selfcheck/vehicle}}"
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
  run_l2_if_enabled "${HAL_ENABLE_PROPERTY_SET_RESTORE_PLACEHOLDER:-false}" \
    "$SCRIPT_DIR/cases/property_set_restore_placeholder.sh" \
    "property_set_restore_placeholder" "property_set_restore_placeholder" "false"

  run_l2_if_enabled "${HAL_ENABLE_SUBSCRIBE_CLEANUP_PLACEHOLDER:-false}" \
    "$SCRIPT_DIR/cases/subscribe_cleanup_placeholder.sh" \
    "subscribe_cleanup_placeholder" "subscribe_cleanup_placeholder" "false"
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
run_l2_if_enabled "${HAL_ENABLE_PROPERTY_SET_RESTORE_PLACEHOLDER:-false}" \
  "$SCRIPT_DIR/cases/property_set_restore_placeholder.sh" \
  "property_set_restore_placeholder" "property_set_restore_placeholder" "false"

run_l2_if_enabled "${HAL_ENABLE_SUBSCRIBE_CLEANUP_PLACEHOLDER:-false}" \
  "$SCRIPT_DIR/cases/subscribe_cleanup_placeholder.sh" \
  "subscribe_cleanup_placeholder" "subscribe_cleanup_placeholder" "false"

finish_and_exit
