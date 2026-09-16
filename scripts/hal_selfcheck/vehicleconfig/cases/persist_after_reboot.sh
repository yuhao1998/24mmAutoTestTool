#!/system/bin/sh
#
# persist_after_reboot — 重启后持久化校验（两阶段）
#
# 用法：
#   阶段1（重启前）: VC_PHASE=pre  sh persist_after_reboot.sh
#   设备重启并回连后
#   阶段2（重启后）: VC_PHASE=post sh persist_after_reboot.sh
#
# pre：测前快照后写入，不还原（保留写入供重启验证）
# post：断言持久化后写回测前快照并再读确认
#
CASE_ID="persist_after_reboot"
SCRIPT_DIR=$(cd "$(dirname "$0")/.." && pwd)
RESULT_DIR="${RESULT_DIR:-$SCRIPT_DIR/report/manual}"
TEST_BIN="${TEST_BIN:-/vendor/bin/hw/vehicleconfigTest}"
CONFIG="${VC_TEST_CONFIG:-$SCRIPT_DIR/default_config.txt}"
VENDOR_FIXED_CFG="/vendor/1190.txt"
PHASE="${VC_PHASE:-pre}"
RESTORE_STRICT=1

# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/case_framework.sh"
# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/snapshot_helpers.sh"
# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/vc_snapshot.sh"

CASE_WORKDIR="$RESULT_DIR/work_${CASE_ID}"
MARKER="$CASE_WORKDIR/marker.env"
INVOKE_LOG="$CASE_WORKDIR/invoke_${PHASE}.log"
READALL_LOG="$CASE_WORKDIR/readall_${PHASE}.log"
CONFIG_IDS=""
CONFIG_ID_COUNT=0

parse_config_ids() {
  CONFIG_IDS=$(tr ',' '\n' < "$CONFIG" 2>/dev/null | grep 'configID' \
    | sed 's/.*"configID"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/' \
    | grep '^[0-9][0-9]*$')
  CONFIG_ID_COUNT=0
  for _id in $CONFIG_IDS; do
    CONFIG_ID_COUNT=$((CONFIG_ID_COUNT + 1))
  done
}

read_all_count() {
  out="$1"
  {
    echo c
    echo g
    for id in $CONFIG_IDS; do
      echo o
      echo "$id"
    done
    echo e
  } > "$CASE_WORKDIR/readall_input.txt"
  cat "$CASE_WORKDIR/readall_input.txt" | "$TEST_BIN" > "$out" 2>&1 || true
  n=$(grep -cE '\[PASS\] readVehicleConfig' "$out" 2>/dev/null || true)
  [ -n "$n" ] || n=0
  echo "$n"
}

hook_prepare() {
  if [ ! -f "$TEST_BIN" ]; then
    case_log "missing TEST_BIN"
    return 1
  fi
  if [ ! -f "$CONFIG" ]; then
    case_log "missing CONFIG=$CONFIG"
    return 1
  fi
  parse_config_ids
  if [ "$CONFIG_ID_COUNT" -le 0 ]; then
    case_log "no configID"
    return 1
  fi
  mkdir -p "$CASE_WORKDIR" 2>/dev/null || true
  if [ "$PHASE" = "post" ] && [ ! -f "$MARKER" ]; then
    case_log "missing marker for post phase: $MARKER"
    return 1
  fi
  if [ "$PHASE" = "pre" ]; then
    if ! vc_snapshot_save 0; then
      return 1
    fi
  fi
  case_log "phase=$PHASE ids=$CONFIG_ID_COUNT"
  return 0
}

hook_invoke() {
  if [ "$PHASE" = "pre" ]; then
    if ! cp "$CONFIG" "$VENDOR_FIXED_CFG" 2>/dev/null; then
      case_log "cannot write $VENDOR_FIXED_CFG"
      return 1
    fi
    {
      echo j
      echo "$CONFIG"
      echo e
    } > "$CASE_WORKDIR/invoke_input.txt"
    cat "$CASE_WORKDIR/invoke_input.txt" | "$TEST_BIN" > "$INVOKE_LOG" 2>&1 || true
    cp "$INVOKE_LOG" "$RESULT_DIR/logs/${CASE_ID}_invoke_pre.log" 2>/dev/null || true
    pass_n=$(read_all_count "$READALL_LOG")
    {
      echo "PRE_PASS_N=$pass_n"
      echo "CONFIG_ID_COUNT=$CONFIG_ID_COUNT"
      echo "CONFIG=$CONFIG"
    } > "$MARKER"
    case_log "wrote marker PRE_PASS_N=$pass_n — reboot device then run VC_PHASE=post"
    return 0
  fi

  pass_n=$(read_all_count "$READALL_LOG")
  echo "POST_PASS_N=$pass_n" >> "$MARKER"
  cp "$READALL_LOG" "$RESULT_DIR/logs/${CASE_ID}_readall_post.log" 2>/dev/null || true
  case_log "post read pass_n=$pass_n"
  return 0
}

hook_assert() {
  if [ "$PHASE" = "pre" ]; then
    pre=$(grep '^PRE_PASS_N=' "$MARKER" 2>/dev/null | cut -d= -f2)
    if [ -n "$pre" ] && [ "$pre" -ge "$CONFIG_ID_COUNT" ]; then
      case_append_detail "pre_ok pass=$pre/$CONFIG_ID_COUNT;await_reboot"
      return 0
    fi
    case_append_detail "pre_readback_fail pass=$pre expected=$CONFIG_ID_COUNT"
    return 1
  fi

  pre=$(grep '^PRE_PASS_N=' "$MARKER" 2>/dev/null | cut -d= -f2)
  post=$(grep '^POST_PASS_N=' "$MARKER" 2>/dev/null | cut -d= -f2)
  if [ -z "$pre" ] || [ -z "$post" ]; then
    case_append_detail "marker_incomplete"
    return 1
  fi
  if [ "$post" -ge "$CONFIG_ID_COUNT" ] && [ "$post" -ge "$pre" ]; then
    case_append_detail "persist_ok pre=$pre post=$post"
    return 0
  fi
  case_append_detail "persist_mismatch pre=$pre post=$post expected=$CONFIG_ID_COUNT"
  return 1
}

hook_restore() {
  if [ "$PHASE" != "post" ]; then
    case_log "skip restore in pre phase (keep written config for reboot check)"
    return 0
  fi
  vc_restore_from_snapshot
}

case_run_four_steps
