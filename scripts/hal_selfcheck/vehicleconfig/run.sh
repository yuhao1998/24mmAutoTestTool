#!/system/bin/sh
# HalSelfCheck entry — vehicleconfig
# 契约：写 $OUT/module_result.json；exit 0=Pass；非 0=Fail
# 规格：docs/templates/module_test_spec.vehicleconfig.yaml
#
MODULE_ID="vehicleconfig"
MODULE_NAME="车参 HAL"
HAL_BINARY="/vendor/bin/hw/vendor.iauto.hardware.vehicleconfig@1.0-service"
PROCESS_PAT="vehicleconfig@1.0-service"
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
OUT="${1:-${T2_SELFCHECK_OUT:-/data/local/tmp/t2_selfcheck/vehicleconfig}}"
export RESULT_DIR="$OUT"
export TEST_BIN="${TEST_BIN:-/vendor/bin/hw/vehicleconfigTest}"
export VC_TEST_CONFIG="${VC_TEST_CONFIG:-$SCRIPT_DIR/default_config.txt}"

mkdir -p "$OUT/cases" "$OUT/logs" "$OUT/work_l1"
STARTED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)

PASS_N=0
FAIL_N=0
SKIP_N=0
CASES_JSON=""

append_case() {
  # $1=id $2=name $3=level $4=status $5=required $6=message
  id="$1"; name="$2"; level="$3"; st="$4"; req="$5"; msg="$6"
  case "$st" in
    pass) PASS_N=$((PASS_N + 1)) ;;
    fail|error) FAIL_N=$((FAIL_N + 1)) ;;
    *) SKIP_N=$((SKIP_N + 1)) ;;
  esac
  msg_esc=$(printf '%s' "$msg" | sed 's/\\/\\\\/g; s/"/\\"/g')
  name_esc=$(printf '%s' "$name" | sed 's/"/\\"/g')
  if [ -n "$CASES_JSON" ]; then
    CASES_JSON="${CASES_JSON},"
  fi
  CASES_JSON="${CASES_JSON}{\"id\":\"${id}\",\"name\":\"${name_esc}\",\"level\":\"${level}\",\"status\":\"${st}\",\"required\":${req},\"message\":\"${msg_esc}\"}"
}

append_from_case_json() {
  # $1=json path $2=display name $3=level $4=required(true/false)
  f="$1"
  name="$2"
  level="$3"
  req="$4"
  if [ ! -f "$f" ]; then
    append_case "$(basename "$f" .json)" "$name" "$level" "fail" "$req" "missing case result json"
    return
  fi
  id=$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)
  st=$(sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)
  dt=$(sed -n 's/.*"detail"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)
  case "$st" in
    PASS|pass) st_norm="pass" ;;
    SKIP|skip) st_norm="skip" ;;
    WARN|warn) st_norm="warn" ;;
    *) st_norm="fail" ;;
  esac
  [ -n "$id" ] || id="$(basename "$f" .json)"
  append_case "$id" "$name" "$level" "$st_norm" "$req" "$dt"
}

is_truthy() {
  case "$1" in
    1|true|TRUE|yes|YES|on|ON) return 0 ;;
    *) return 1 ;;
  esac
}

run_l2_if_enabled() {
  # $1=enabled(true/false/1) $2=script $3=case_id $4=name $5=required
  en="$1"; script="$2"; cid="$3"; name="$4"; req="$5"
  if ! is_truthy "$en"; then
    append_case "$cid" "$name" "L2" "skip" "$req" "disabled in manifest (set VC_ENABLE_* =1 to run)"
    return
  fi
  if [ ! -f "$script" ]; then
    append_case "$cid" "$name" "L2" "fail" "$req" "missing $script"
    return
  fi
  chmod 755 "$script" 2>/dev/null || true
  sh "$script"
  append_from_case_json "$OUT/cases/${cid}.json" "$name" "L2" "$req"
}

# ---------- L1: ipc ----------
if [ -e "$HAL_BINARY" ]; then
  append_case "ipc" "服务可连接" "L1" "pass" "true" "binary exists: $HAL_BINARY"
else
  append_case "ipc" "服务可连接" "L1" "fail" "true" "binary missing: $HAL_BINARY"
fi

# ---------- L1: binder ----------
SLOT_LOG="$OUT/work_l1/slot_query.log"
PID1=""
PID2=""
if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
  PID1=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
  PID1=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
fi
if [ -n "$PID1" ]; then
  append_case "binder" "服务绑定正常" "L1" "pass" "true" "process alive: $PROCESS_PAT pid=$PID1"
else
  append_case "binder" "服务绑定正常" "L1" "fail" "true" "process not found: $PROCESS_PAT"
fi

# ---------- L1: response + function_smoke + no_crash（依赖 test bin）----------
if [ ! -f "$TEST_BIN" ]; then
  append_case "response" "接口有响应" "L1" "fail" "true" "missing TEST_BIN=$TEST_BIN"
  append_case "function_smoke" "功能冒烟(槽位可读)" "L1" "fail" "true" "missing TEST_BIN"
  append_case "no_crash" "调用后无崩溃" "L1" "fail" "true" "missing TEST_BIN; cannot probe"
else
  printf 'g\ne\n' > "$OUT/work_l1/slot_input.txt"
  cat "$OUT/work_l1/slot_input.txt" | "$TEST_BIN" > "$SLOT_LOG" 2>&1 || true
  cp "$SLOT_LOG" "$OUT/logs/l1_slot_query.log" 2>/dev/null || true

  if grep -E '当前活动槽位|CONFIG_SLOT_[AB]' "$SLOT_LOG" >/dev/null 2>&1; then
    append_case "response" "接口有响应" "L1" "pass" "true" "slot query returned status tags"
  else
    append_case "response" "接口有响应" "L1" "fail" "true" "no slot response tags in log"
  fi

  if grep -E '当前活动槽位: [AB] 面|CONFIG_SLOT_[AB]' "$SLOT_LOG" >/dev/null 2>&1; then
    append_case "function_smoke" "功能冒烟(槽位可读)" "L1" "pass" "true" "slot field non-empty"
  else
    append_case "function_smoke" "功能冒烟(槽位可读)" "L1" "fail" "true" "slot field not parseable"
  fi

  sleep 1
  if pidof "$PROCESS_PAT" >/dev/null 2>&1; then
    PID2=$(pidof "$PROCESS_PAT" 2>/dev/null | awk '{print $1}')
  elif pgrep -f "$PROCESS_PAT" >/dev/null 2>&1; then
    PID2=$(pgrep -f "$PROCESS_PAT" 2>/dev/null | head -n 1)
  fi

  TB_HIT=$(ls /data/tombstones 2>/dev/null | head -n 8)
  if [ -z "$PID2" ]; then
    append_case "no_crash" "调用后无崩溃" "L1" "fail" "true" "process gone after slot query"
  elif echo "$TB_HIT" | grep -qi "vehicleconfig"; then
    append_case "no_crash" "调用后无崩溃" "L1" "fail" "true" "tombstone name may relate to vehicleconfig"
  elif [ -n "$PID1" ] && [ "$PID1" != "$PID2" ]; then
    append_case "no_crash" "调用后无崩溃" "L1" "warn" "true" "pid changed $PID1->$PID2 (restarted?)"
  else
    append_case "no_crash" "调用后无崩溃" "L1" "pass" "true" "process alive pid=${PID2}"
  fi
fi

append_case "memleak_probe" "无内存泄漏（探针）" "L1" "skip" "false" "disabled in manifest"

# ---------- L2 cases（enabled 与 manifest 对齐；可用环境变量强制开启）----------
# VC_ENABLE_<CASE>=1 可临时打开未默认启用的专项
chmod 755 "$SCRIPT_DIR/lib/case_framework.sh" 2>/dev/null || true

EN_WRR=true
EN_INV="${VC_ENABLE_WRITE_INVALID:-false}"
EN_RID="${VC_ENABLE_READ_INVALID:-false}"
EN_PERSIST="${VC_ENABLE_PERSIST_REBOOT:-false}"
EN_CONC="${VC_ENABLE_CONCURRENT:-false}"

run_l2_if_enabled "$EN_WRR" \
  "$SCRIPT_DIR/cases/write_read_restore.sh" \
  "write_read_restore" "合规写入+切面+全量查询+还原" "true"

run_l2_if_enabled "$EN_INV" \
  "$SCRIPT_DIR/cases/write_invalid_expected_fail.sh" \
  "write_invalid_expected_fail" "空文件/不合规文件写入_预期失败" "true"

run_l2_if_enabled "$EN_RID" \
  "$SCRIPT_DIR/cases/read_invalid_configid.sh" \
  "read_invalid_configid" "不合规车参名读取_预期失败" "true"

run_l2_if_enabled "$EN_PERSIST" \
  "$SCRIPT_DIR/cases/persist_after_reboot.sh" \
  "persist_after_reboot" "重启后持久化校验" "false"

run_l2_if_enabled "$EN_CONC" \
  "$SCRIPT_DIR/cases/concurrent_query.sh" \
  "concurrent_query" "多进程并发查询压测" "false"

FINISHED=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)
if [ "$FAIL_N" -gt 0 ]; then
  OVERALL="fail"
else
  OVERALL="pass"
fi
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
  "cases": [ ${CASES_JSON} ],
  "attachments": []
}
EOF

echo "MODULE=${MODULE_ID} OVERALL=${OVERALL} ${SUMMARY}"
echo "RESULT=$OUT/module_result.json"
if [ "$OVERALL" = "pass" ]; then
  echo "Pass"
  exit 0
fi
echo "Failed"
exit 1
