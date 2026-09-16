#!/system/bin/sh
#
# 通用测前快照助手（全模块共用）
# 约定目录：$CASE_WORKDIR/snapshot/
#   meta.env / values.env / files/ / prepare_get.log / restore_verify.log
#
# 依赖：已 source case_framework.sh（使用 case_log / CASE_WORKDIR）
#

snap_dir() {
  echo "${CASE_WORKDIR}/snapshot"
}

snap_init() {
  d=$(snap_dir)
  mkdir -p "$d/files" 2>/dev/null || true
  {
    echo "CASE_ID=${CASE_ID}"
    echo "SNAPSHOT_TS=$(date '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || echo unknown)"
    echo "RESTORE_STRICT=${RESTORE_STRICT:-1}"
  } > "$d/meta.env"
  : > "$d/values.env"
  : > "$d/prepare_get.log"
  : > "$d/restore_verify.log"
  case_log "snapshot dir=$d"
}

# 转义值中的换行与危险字符为单行（简单替换）
snap_escape() {
  printf '%s' "$1" | tr '\n' ' ' | sed 's/"/\\"/g'
}

# snap_set KEY VALUE — 写入 values.env
snap_set() {
  key="$1"
  val=$(snap_escape "$2")
  d=$(snap_dir)
  # 删除旧键再追加
  if [ -f "$d/values.env" ]; then
    grep -v "^${key}=" "$d/values.env" > "$d/values.env.tmp" 2>/dev/null || true
    mv "$d/values.env.tmp" "$d/values.env" 2>/dev/null || true
  fi
  echo "${key}=${val}" >> "$d/values.env"
}

# snap_get KEY — 打印值；不存在返回 1
snap_get() {
  key="$1"
  d=$(snap_dir)
  [ -f "$d/values.env" ] || return 1
  line=$(grep "^${key}=" "$d/values.env" 2>/dev/null | head -n 1) || return 1
  [ -n "$line" ] || return 1
  echo "${line#*=}"
}

# 备份文件到 snapshot/files/<name>
snap_save_file() {
  src="$1"
  name="$2"
  d=$(snap_dir)
  if [ ! -f "$src" ]; then
    case_log "snap_save_file: missing $src"
    return 1
  fi
  if ! cp "$src" "$d/files/$name" 2>/dev/null; then
    case_log "snap_save_file: cannot copy $src"
    return 1
  fi
  snap_set "file:${name}" "$src"
  case_log "saved file snapshot $src -> files/$name"
  return 0
}

# 从 snapshot 写回文件到原路径
snap_restore_file() {
  name="$1"
  dest="$2"
  d=$(snap_dir)
  src="$d/files/$name"
  if [ ! -f "$src" ]; then
    case_log "snap_restore_file: no snapshot files/$name"
    return 1
  fi
  if ! cp "$src" "$dest" 2>/dev/null; then
    case_log "snap_restore_file: cannot write $dest"
    return 1
  fi
  case_log "restored file $dest from snapshot/$name"
  return 0
}

# cmp 确认文件与快照一致
snap_verify_file() {
  name="$1"
  dest="$2"
  d=$(snap_dir)
  src="$d/files/$name"
  if [ ! -f "$src" ] || [ ! -f "$dest" ]; then
    case_log "snap_verify_file: missing src or dest"
    return 1
  fi
  if cmp -s "$src" "$dest" 2>/dev/null; then
    case_log "file verify OK: $dest matches snapshot"
    return 0
  fi
  case_log "file verify FAIL: $dest != snapshot/$name"
  return 1
}

# 要求 values.env 中存在若干键
snap_require_keys() {
  for k in "$@"; do
    if ! snap_get "$k" >/dev/null 2>&1; then
      case_log "missing snapshot key: $k"
      return 1
    fi
  done
  return 0
}
