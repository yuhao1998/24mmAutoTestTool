#!/system/bin/sh
#
# concurrent_query — 多进程并发只读查询压测
#
CASE_ID="concurrent_query"
SCRIPT_DIR=$(cd "$(dirname "$0")/.." && pwd)
RESULT_DIR="${RESULT_DIR:-$SCRIPT_DIR/report/manual}"
TEST_BIN="${TEST_BIN:-/vendor/bin/hw/vehicleconfigTest}"
CONFIG="${VC_TEST_CONFIG:-$SCRIPT_DIR/default_config.txt}"
PROCESS_PAT="vehicleconfig@1.0-service"
NPROC="${VC_CONCURRENT_N:-4}"
RESTORE_STRICT=0

# shellcheck source=/dev/null
. "$SCRIPT_DIR/lib/case_framework.sh"

CASE_WORKDIR="$RESULT_DIR/work_${CASE_ID}"
CONFIG_IDS=""
CONFIG_ID_COUNT=0
PID_BEFORE=""

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
    case_log "missing TEST_BIN"
    return 1
  fi
  if [ ! -f "$CONFIG" ]; then
    case_log "missing CONFIG"
    return 1
  fi
  parse_config_ids
  if [ "$CONFIG_ID_COUNT" -le 0 ]; then
    case_log "no configID"
    return 1
  fi
  if [ "$NPROC" -lt 2 ]; then
    NPROC=2
  fi
  if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
    PID_BEFORE=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
  elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
    PID_BEFORE=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
  fi
  case_log "nproc=$NPROC ids=$CONFIG_ID_COUNT pid=$PID_BEFORE"
  return 0
}

worker_read() {
  wid="$1"
  out="$CASE_WORKDIR/worker_${wid}.log"
  {
    echo c
    echo g
    # 每个 worker 读全部 id（只读压力）
    for id in $CONFIG_IDS; do
      echo o
      echo "$id"
    done
    echo e
  } > "$CASE_WORKDIR/worker_${wid}_input.txt"
  cat "$CASE_WORKDIR/worker_${wid}_input.txt" | "$TEST_BIN" > "$out" 2>&1 || true
}

hook_invoke() {
  i=1
  while [ "$i" -le "$NPROC" ]; do
    worker_read "$i" &
    i=$((i + 1))
  done
  wait
  case_log "all $NPROC workers finished"
  return 0
}

hook_assert() {
  if grep -E 'FATAL|Segmentation' "$CASE_WORKDIR"/worker_*.log >/dev/null 2>&1; then
    case_append_detail "crash_signature_in_worker_log"
    return 1
  fi
  pid_after=""
  if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
    pid_after=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
  elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
    pid_after=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
  fi
  if [ -z "$pid_after" ]; then
    case_append_detail "process_dead_after_concurrent"
    return 1
  fi

  ok_workers=0
  i=1
  while [ "$i" -le "$NPROC" ]; do
    f="$CASE_WORKDIR/worker_${i}.log"
    pass_n=$(grep -cE '\[PASS\] readVehicleConfig' "$f" 2>/dev/null) || pass_n=0
    fail_n=$(grep -cE 'get config fail!|\[FAIL\] readVehicleConfig' "$f" 2>/dev/null) || fail_n=0
    cp "$f" "$RESULT_DIR/logs/${CASE_ID}_worker_${i}.log" 2>/dev/null || true
    if [ "$fail_n" -eq 0 ] && [ "$pass_n" -ge "$CONFIG_ID_COUNT" ]; then
      ok_workers=$((ok_workers + 1))
    fi
    i=$((i + 1))
  done

  if [ "$ok_workers" -eq "$NPROC" ]; then
    case_append_detail "concurrent_ok workers=$ok_workers/$NPROC"
    return 0
  fi
  case_append_detail "concurrent_partial ok=$ok_workers/$NPROC"
  return 1
}

hook_restore() {
  return 0
}

case_run_four_steps
