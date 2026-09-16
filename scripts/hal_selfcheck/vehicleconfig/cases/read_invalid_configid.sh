#!/system/bin/sh
#
# read_invalid_configid — 读取不合规/不存在 configID，预期失败且不影响后续
#
CASE_ID="read_invalid_configid"
SCRIPT_DIR=$(cd "$(dirname "$0")/.." && pwd)
RESULT_DIR="${RESULT_DIR:-$SCRIPT_DIR/report/manual}"
TEST_BIN="${TEST_BIN:-/vendor/bin/hw/vehicleconfigTest}"
BAD_ID="${VC_BAD_CONFIG_ID:-999999999}"
PROCESS_PAT="vehicleconfig@1.0-service"
RESTORE_STRICT=0

# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/case_framework.sh"

CASE_WORKDIR="$RESULT_DIR/work_${CASE_ID}"
INVOKE_LOG="$CASE_WORKDIR/invoke.log"
PID_BEFORE=""

hook_prepare() {
  if [ ! -f "$TEST_BIN" ]; then
    case_log "missing TEST_BIN=$TEST_BIN"
    return 1
  fi
  if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
    PID_BEFORE=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
  elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
    PID_BEFORE=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
  fi
  case_log "bad_config_id=$BAD_ID pid_before=$PID_BEFORE"
  return 0
}

hook_invoke() {
  {
    echo c
    echo g
    echo o
    echo "$BAD_ID"
    echo e
  } > "$CASE_WORKDIR/invoke_input.txt"
  cat "$CASE_WORKDIR/invoke_input.txt" | "$TEST_BIN" > "$INVOKE_LOG" 2>&1 || true
  cp "$INVOKE_LOG" "$RESULT_DIR/logs/${CASE_ID}_invoke.log" 2>/dev/null || true
  return 0
}

hook_assert() {
  if grep -E 'FATAL|Segmentation' "$INVOKE_LOG" >/dev/null 2>&1; then
    case_append_detail "crash_signature"
    return 1
  fi
  pid_after=""
  if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
    pid_after=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
  elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
    pid_after=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
  fi
  if [ -z "$pid_after" ]; then
    case_append_detail "process_dead"
    return 1
  fi

  # 预期：读失败标签，或没有对该 id 的 PASS
  if grep -E "get config fail!|\[FAIL\] readVehicleConfig|not found|invalid|Invalid" "$INVOKE_LOG" >/dev/null 2>&1; then
    case_append_detail "expected_fail_ok;pid=$pid_after"
    return 0
  fi
  if grep -E "\[PASS\] readVehicleConfig" "$INVOKE_LOG" >/dev/null 2>&1; then
    case_append_detail "unexpected_pass_for_bad_id"
    return 1
  fi
  # 无明确 PASS 也视为「未成功读到」，记为符合预期失败语义
  case_append_detail "no_pass_for_bad_id;pid=$pid_after"
  return 0
}

hook_restore() {
  # 只读负面用例，无需还原
  return 0
}

case_run_four_steps
