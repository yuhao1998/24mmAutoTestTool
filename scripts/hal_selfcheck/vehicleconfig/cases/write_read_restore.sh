#!/system/bin/sh
#
# write_read_restore — 写车参 → 全量读回校验 → 还原测前初始值
# 严格四步：PREPARE / INVOKE / ASSERT / RESTORE
# 对齐 L2/担当：测前快照（文件+槽位）→ 测后写回 → 再查槽确认；失败=Fail
#
# 依赖：
#   /vendor/bin/hw/vehicleconfigTest
#   同目录上级 default_config.txt（或 VC_TEST_CONFIG）
#

CASE_ID="write_read_restore"
SCRIPT_DIR=$(cd "$(dirname "$0")/.." && pwd)
RESULT_DIR="${RESULT_DIR:-$SCRIPT_DIR/report/manual}"
TEST_BIN="${TEST_BIN:-/vendor/bin/hw/vehicleconfigTest}"
VENDOR_FIXED_CFG="/vendor/1190.txt"
CONFIG="${VC_TEST_CONFIG:-$SCRIPT_DIR/default_config.txt}"
RESTORE_STRICT=1

# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/case_framework.sh"
# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/snapshot_helpers.sh"
# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/vc_snapshot.sh"

CASE_WORKDIR="$RESULT_DIR/work_${CASE_ID}"
INVOKE_LOG="$CASE_WORKDIR/invoke.log"
READALL_LOG="$CASE_WORKDIR/readall.log"
CONFIG_ID_COUNT=0
CONFIG_IDS=""

parse_config_ids() {
  CONFIG_IDS=$(tr ',' '\n' < "$CONFIG" 2>/dev/null | grep 'configID' \
    | sed 's/.*"configID"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/' \
    | grep '^[0-9][0-9]*$')
  CONFIG_ID_COUNT=0
  for _id in $CONFIG_IDS; do
    CONFIG_ID_COUNT=$((CONFIG_ID_COUNT + 1))
  done
}

hook_prepare() {
  if [ ! -f "$TEST_BIN" ]; then
    case_log "missing TEST_BIN=$TEST_BIN"
    return 1
  fi
  if [ ! -f "$CONFIG" ]; then
    case_log "missing CONFIG=$CONFIG"
    return 1
  fi
  first_char=$(head -c 1 "$CONFIG" 2>/dev/null)
  if [ "$first_char" != "{" ]; then
    case_log "invalid config (not plain JSON), maybe encrypted"
    return 1
  fi
  parse_config_ids
  if [ "$CONFIG_ID_COUNT" -le 0 ]; then
    case_log "no configID in $CONFIG"
    return 1
  fi
  case_log "config=$CONFIG ids=$CONFIG_ID_COUNT"

  # 测前保存初始值（vendor 文件尽力备份；槽位必须记下）
  if ! vc_snapshot_save 0; then
    case_log "snapshot save failed"
    return 1
  fi
  vc_dump_prepare_readlog "$CONFIG_IDS"
  return 0
}

hook_invoke() {
  if ! cp "$CONFIG" "$VENDOR_FIXED_CFG" 2>/dev/null; then
    case_log "cannot write $VENDOR_FIXED_CFG (need adb root/remount)"
    return 1
  fi
  case_log "synced test config to $VENDOR_FIXED_CFG"

  {
    echo j
    echo "$CONFIG"
    echo e
  } > "$CASE_WORKDIR/invoke_input.txt"

  cat "$CASE_WORKDIR/invoke_input.txt" | "$TEST_BIN" > "$INVOKE_LOG" 2>&1 || true
  cp "$INVOKE_LOG" "$RESULT_DIR/logs/${CASE_ID}_invoke.log" 2>/dev/null || true

  if grep -F '[PASS] oneClickUpgrade' "$INVOKE_LOG" >/dev/null 2>&1 \
     || grep -F 'writeVehicleConfig configStatus == 0' "$INVOKE_LOG" >/dev/null 2>&1 \
     || grep -F 'setActiveSlot success' "$INVOKE_LOG" >/dev/null 2>&1; then
    case_log "write/switch invoked (see invoke log)"
    return 0
  fi

  if grep -E '\[FAIL\] (oneClickUpgrade|writeVehicleConfig)' "$INVOKE_LOG" >/dev/null 2>&1; then
    case_log "explicit FAIL in invoke log"
    return 1
  fi
  case_log "invoke finished without clear PASS tag; defer to ASSERT"
  return 0
}

hook_assert() {
  {
    echo c
    echo g
    for id in $CONFIG_IDS; do
      echo o
      echo "$id"
    done
    echo e
  } > "$CASE_WORKDIR/readall_input.txt"

  cat "$CASE_WORKDIR/readall_input.txt" | "$TEST_BIN" > "$READALL_LOG" 2>&1 || true
  cp "$READALL_LOG" "$RESULT_DIR/logs/${CASE_ID}_readall.log" 2>/dev/null || true

  read_pass=$(grep -cE '\[PASS\] readVehicleConfig' "$READALL_LOG" 2>/dev/null) || read_pass=0
  read_fail=$(grep -cE 'get config fail!|\[FAIL\] readVehicleConfig' "$READALL_LOG" 2>/dev/null) || read_fail=0

  case_log "read_all pass=$read_pass fail=$read_fail expected=$CONFIG_ID_COUNT"

  if [ "$read_fail" -eq 0 ] && [ "$read_pass" -ge "$CONFIG_ID_COUNT" ]; then
    case_append_detail "write_ok;read_all_ok pass=$read_pass/$CONFIG_ID_COUNT"
    return 0
  fi

  case_append_detail "read_all_mismatch pass=$read_pass fail=$read_fail expected=$CONFIG_ID_COUNT"
  return 1
}

hook_restore() {
  vc_restore_from_snapshot
}

case_run_four_steps
