#!/system/bin/sh
#
# write_invalid_expected_fail — 空/不合规文件写入，预期失败且不崩溃
# 四步：PREPARE 测前快照 / INVOKE / ASSERT / RESTORE 写回初始值并确认
#
CASE_ID="write_invalid_expected_fail"
SCRIPT_DIR=$(cd "$(dirname "$0")/.." && pwd)
RESULT_DIR="${RESULT_DIR:-$SCRIPT_DIR/report/manual}"
TEST_BIN="${TEST_BIN:-/vendor/bin/hw/vehicleconfigTest}"
VENDOR_FIXED_CFG="/vendor/1190.txt"
EMPTY_CFG="${VC_INVALID_CONFIG:-$SCRIPT_DIR/fixtures/empty.txt}"
PROCESS_PAT="vehicleconfig@1.0-service"
RESTORE_STRICT=1

# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/case_framework.sh"
# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/snapshot_helpers.sh"
# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/vc_snapshot.sh"

CASE_WORKDIR="$RESULT_DIR/work_${CASE_ID}"
INVOKE_LOG="$CASE_WORKDIR/invoke.log"
PID_BEFORE=""

hook_prepare() {
  if [ ! -f "$TEST_BIN" ]; then
    case_log "missing TEST_BIN=$TEST_BIN"
    return 1
  fi
  if [ ! -f "$EMPTY_CFG" ]; then
    mkdir -p "$(dirname "$EMPTY_CFG")" 2>/dev/null || true
    : > "$EMPTY_CFG"
    case_log "created empty fixture $EMPTY_CFG"
  fi
  if ! vc_snapshot_save 0; then
    return 1
  fi
  if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
    PID_BEFORE=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
  elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
    PID_BEFORE=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
  fi
  return 0
}

hook_invoke() {
  if ! cp "$EMPTY_CFG" "$VENDOR_FIXED_CFG" 2>/dev/null; then
    case_log "cannot write $VENDOR_FIXED_CFG (need root/remount)"
    return 1
  fi
  {
    echo j
    echo "$EMPTY_CFG"
    echo e
  } > "$CASE_WORKDIR/invoke_input.txt"
  cat "$CASE_WORKDIR/invoke_input.txt" | "$TEST_BIN" > "$INVOKE_LOG" 2>&1 || true
  cp "$INVOKE_LOG" "$RESULT_DIR/logs/${CASE_ID}_invoke.log" 2>/dev/null || true
  case_log "invalid write invoked"
  return 0
}

hook_assert() {
  hit_fail=0
  if grep -E '\[FAIL\] (oneClickUpgrade|writeVehicleConfig)|invalid|Invalid|error|Error|fail|Fail' "$INVOKE_LOG" >/dev/null 2>&1; then
    hit_fail=1
  fi
  hit_pass=0
  if grep -F '[PASS] oneClickUpgrade' "$INVOKE_LOG" >/dev/null 2>&1; then
    hit_pass=1
  fi

  pid_after=""
  if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
    pid_after=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
  elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
    pid_after=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
  fi

  if grep -E 'FATAL|Segmentation|tombstone' "$INVOKE_LOG" >/dev/null 2>&1; then
    case_append_detail "crash_signature_in_log"
    return 1
  fi
  if [ -z "$pid_after" ]; then
    case_append_detail "process_dead_after_invalid_write"
    return 1
  fi
  if [ "$hit_pass" -eq 1 ] && [ "$hit_fail" -eq 0 ]; then
    case_append_detail "unexpected_pass_on_invalid_input"
    return 1
  fi
  if [ "$hit_fail" -eq 1 ] || [ "$hit_pass" -eq 0 ]; then
    case_append_detail "expected_fail_ok;pid=$pid_after"
    return 0
  fi
  case_append_detail "ambiguous_result"
  return 1
}

hook_restore() {
  vc_restore_from_snapshot
}

case_run_four_steps
