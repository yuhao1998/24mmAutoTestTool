#!/system/bin/sh
#
# HAL 模块通用 case 模板（四步法）
# 复制本文件为 cases/xxx.sh，实现四个 hook 即可。
#
# 契约（对齐 L2/担当 R1～R4）：
#   PREPARE — 检查工具；对 mutates 读初始值写入 snapshot/
#   INVOKE  — 调用被测接口/命令
#   ASSERT  — 根据日志/返回值判定 Pass/Fail
#   RESTORE — 写回 snapshot 初始值；再读确认；失败且 RESTORE_STRICT=1 → 用例 Fail
#
# 快照助手：. "$SCRIPT_DIR/lib/snapshot_helpers.sh"（由 _framework 复制）
#

CASE_ID="template.example"
SCRIPT_DIR=$(cd "$(dirname "$0")/.." && pwd)
FRAMEWORK="${SCRIPT_DIR}/lib/case_framework.sh"
if [ ! -f "$FRAMEWORK" ]; then
  FRAMEWORK="${SCRIPT_DIR}/../_framework/case_framework.sh"
fi
RESULT_DIR="${RESULT_DIR:-$SCRIPT_DIR/report/manual}"
RESTORE_STRICT=1

# shellcheck source=/dev/null
. "$FRAMEWORK"
if [ -f "${SCRIPT_DIR}/lib/snapshot_helpers.sh" ]; then
  # shellcheck source=/dev/null
  . "${SCRIPT_DIR}/lib/snapshot_helpers.sh"
elif [ -f "${SCRIPT_DIR}/../_framework/snapshot_helpers.sh" ]; then
  # shellcheck source=/dev/null
  . "${SCRIPT_DIR}/../_framework/snapshot_helpers.sh"
fi

hook_prepare() {
  snap_init
  case_log "TODO: 对每个 mutates 执行 get_how，snap_set KEY VAL 或 snap_save_file"
  return 0
}

hook_invoke() {
  case_log "TODO: 调用测试方法（adb/hidl/test binary）"
  return 0
}

hook_assert() {
  case_log "TODO: 解析输出，失败则 return 1"
  return 0
}

hook_restore() {
  case_log "TODO: 按 snapshot 写回 set_how；再 get 与快照比对；失败 return 1"
  return 0
}

case_run_four_steps
