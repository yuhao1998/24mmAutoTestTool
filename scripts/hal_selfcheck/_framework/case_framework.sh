#!/system/bin/sh
#
# HAL 模块通用用例框架（四步法）
# 每项测试必须按序执行：
#   1) PREPARE  预先准备（环境检查、测前快照、造数）
#   2) INVOKE   测试方法调用
#   3) ASSERT   调用结果判断
#   4) RESTORE  写回测前快照并再读确认（对齐 L2/担当 R4）
#
# 用法（在 case 脚本中）:
#   . "$FRAMEWORK_DIR/case_framework.sh"
#   CASE_ID="biz.xxx"
#   RESTORE_STRICT=1   # 写属性默认 1：还原确认失败 → 用例 Fail
#   case_run_four_steps
#
# 子脚本需实现钩子:
#   hook_prepare / hook_invoke / hook_assert / hook_restore
#

: "${CASE_ID:=unknown}"
: "${RESULT_DIR:=.}"
: "${CASE_WORKDIR:=${RESULT_DIR}/work_${CASE_ID}}"
: "${CASE_STATUS:=PASS}"
: "${CASE_DETAIL:=}"
: "${CASE_STEP:=}"
: "${RESTORE_WARN:=0}"
# 对齐 L2/担当：写属性还原失败默认 Fail；纯只读可设 RESTORE_STRICT=0
: "${RESTORE_STRICT:=1}"

case_init() {
  mkdir -p "$RESULT_DIR/cases" "$RESULT_DIR/logs" "$CASE_WORKDIR" 2>/dev/null || true
  CASE_STATUS="PASS"
  CASE_DETAIL=""
  RESTORE_WARN=0
  CASE_LOG="$RESULT_DIR/logs/${CASE_ID}.log"
  : > "$CASE_LOG" 2>/dev/null || true
  echo "[CASE] $CASE_ID BEGIN"
  echo "[CASE] $CASE_ID BEGIN" >> "$CASE_LOG" 2>/dev/null || true
}

case_log() {
  echo "[$CASE_STEP] $*"
  echo "[$CASE_STEP] $*" >> "$CASE_LOG" 2>/dev/null || true
}

case_append_detail() {
  CASE_DETAIL="${CASE_DETAIL}$1;"
}

# --- 默认可覆盖钩子 ---
hook_prepare() { return 0; }
hook_invoke()  { return 0; }
hook_assert()  { return 0; }
hook_restore() { return 0; }

case_step_prepare() {
  CASE_STEP="PREPARE"
  case_log "=== 1/4 预先准备（测前快照）==="
  if hook_prepare; then
    case_log "PREPARE OK"
    return 0
  fi
  case_log "PREPARE FAIL"
  case_append_detail "prepare_fail"
  CASE_STATUS="FAIL"
  return 1
}

case_step_invoke() {
  CASE_STEP="INVOKE"
  case_log "=== 2/4 测试方法调用 ==="
  if hook_invoke; then
    case_log "INVOKE OK"
    return 0
  fi
  case_log "INVOKE FAIL"
  case_append_detail "invoke_fail"
  CASE_STATUS="FAIL"
  return 1
}

case_step_assert() {
  CASE_STEP="ASSERT"
  case_log "=== 3/4 调用结果判断 ==="
  if hook_assert; then
    case_log "ASSERT OK"
    return 0
  fi
  case_log "ASSERT FAIL"
  case_append_detail "assert_fail"
  CASE_STATUS="FAIL"
  return 1
}

case_step_restore() {
  CASE_STEP="RESTORE"
  case_log "=== 4/4 状态还原（写回快照并确认）==="
  if hook_restore; then
    case_log "RESTORE OK"
    return 0
  fi
  if [ "$RESTORE_STRICT" = "1" ]; then
    case_log "RESTORE FAIL (strict)"
    case_append_detail "restore_fail"
    CASE_STATUS="FAIL"
    return 1
  fi
  case_log "RESTORE WARN"
  case_append_detail "restore_warn"
  RESTORE_WARN=1
  return 1
}

case_fail_exit() {
  # $1 = failed step name；仍尝试还原（失败不掩盖原失败点）
  CASE_STATUS="FAIL"
  case_append_detail "failed_at_$1"
  CASE_STEP="RESTORE"
  if ! hook_restore; then
    if [ "$RESTORE_STRICT" = "1" ]; then
      case_append_detail "restore_fail"
    else
      case_append_detail "restore_warn"
      RESTORE_WARN=1
    fi
  fi
  case_finish
  exit 1
}

case_finish() {
  [ -z "$CASE_DETAIL" ] && CASE_DETAIL="ok"
  OUT="$RESULT_DIR/cases/${CASE_ID}.json"
  detail_esc=$(printf '%s' "$CASE_DETAIL" | sed 's/"/\\"/g')
  cat > "$OUT" <<EOF
{
  "id": "$CASE_ID",
  "status": "$CASE_STATUS",
  "detail": "$detail_esc",
  "steps": ["PREPARE", "INVOKE", "ASSERT", "RESTORE"],
  "restore_warn": $RESTORE_WARN,
  "restore_strict": $RESTORE_STRICT
}
EOF
  echo "[CASE] $CASE_ID $CASE_STATUS $CASE_DETAIL"
  if [ "$CASE_STATUS" = "PASS" ]; then
    return 0
  fi
  return 1
}

# 便捷：跑满四步并结束
case_run_four_steps() {
  case_init
  case_step_prepare || case_fail_exit "prepare"
  case_step_invoke  || case_fail_exit "invoke"
  case_step_assert  || case_fail_exit "assert"
  case_step_restore || true
  case_finish
  [ "$CASE_STATUS" = "PASS" ] && exit 0 || exit 1
}
