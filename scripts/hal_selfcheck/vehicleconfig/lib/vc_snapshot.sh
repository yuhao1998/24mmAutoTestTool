#!/system/bin/sh
#
# vehicleconfig 测前快照 / 测后还原（基于 snapshot_helpers）
# 初始值：/vendor/1190.txt 整包 + 活动槽位(1=A,2=B)
# 还原确认：写回文件 + i 装回 + s 切槽后，再查询槽位比对（不依赖 success 日志标签）
#

: "${TEST_BIN:=/vendor/bin/hw/vehicleconfigTest}"
: "${VENDOR_FIXED_CFG:=/vendor/1190.txt}"

vc_query_slot() {
  # 输出 1/2/0 到 stdout
  work="${CASE_WORKDIR}/slot_query"
  mkdir -p "$work" 2>/dev/null || true
  printf 'g\ne\n' > "$work/input.txt"
  cat "$work/input.txt" | "$TEST_BIN" > "$work/out.log" 2>&1 || true
  if grep -E '当前活动槽位: A 面|CONFIG_SLOT_A' "$work/out.log" >/dev/null 2>&1; then
    echo 1
  elif grep -E '当前活动槽位: B 面|CONFIG_SLOT_B' "$work/out.log" >/dev/null 2>&1; then
    echo 2
  else
    echo 0
  fi
}

# 可选：对 CONFIG_IDS 做一次只读审计，写入 prepare_get.log
vc_dump_prepare_readlog() {
  d=$(snap_dir)
  ids="$1"
  {
    echo c
    echo g
    for id in $ids; do
      echo o
      echo "$id"
    done
    echo e
  } > "$CASE_WORKDIR/prepare_read_input.txt"
  cat "$CASE_WORKDIR/prepare_read_input.txt" | "$TEST_BIN" > "$d/prepare_get.log" 2>&1 || true
  n=$(grep -cE '\[PASS\] readVehicleConfig' "$d/prepare_get.log" 2>/dev/null || true)
  [ -n "$n" ] || n=0
  snap_set "prepare_read_pass" "$n"
  case_log "prepare read audit pass_n=$n"
}

# 测前保存初始值：文件（若存在）+ 槽位
# $1 = require_file：1 表示 vendor 文件必须能备份，否则失败
vc_snapshot_save() {
  require_file="${1:-0}"
  snap_init

  if [ -f "$VENDOR_FIXED_CFG" ]; then
    if snap_save_file "$VENDOR_FIXED_CFG" "1190.txt"; then
      snap_set "HAD_VENDOR_BACKUP" "1"
    else
      case_log "cannot backup $VENDOR_FIXED_CFG (need root/remount)"
      if [ "$require_file" = "1" ]; then
        return 1
      fi
      snap_set "HAD_VENDOR_BACKUP" "0"
    fi
  else
    case_log "no existing $VENDOR_FIXED_CFG (first-write scenario)"
    snap_set "HAD_VENDOR_BACKUP" "0"
    if [ "$require_file" = "1" ]; then
      case_log "require_file=1 but vendor config missing"
      return 1
    fi
  fi

  slot=$(vc_query_slot)
  snap_set "ORIG_SLOT" "$slot"
  echo "$slot" > "$(snap_dir)/orig_slot.txt"
  case_log "initial snapshot: HAD_VENDOR_BACKUP=$(snap_get HAD_VENDOR_BACKUP) ORIG_SLOT=$slot"
  return 0
}

# 写回初始值并再读确认槽位
vc_restore_from_snapshot() {
  ok=1
  d=$(snap_dir)
  had=$(snap_get HAD_VENDOR_BACKUP 2>/dev/null || echo 0)
  want_slot=$(snap_get ORIG_SLOT 2>/dev/null || echo 0)
  if [ -f "$d/orig_slot.txt" ]; then
    want_slot=$(cat "$d/orig_slot.txt" 2>/dev/null || echo "$want_slot")
  fi

  restore_log="$d/restore_verify.log"
  : > "$restore_log"

  if [ "$had" = "1" ]; then
    if ! snap_restore_file "1190.txt" "$VENDOR_FIXED_CFG"; then
      ok=0
    else
      {
        echo i
        echo "$d/files/1190.txt"
        if [ "$want_slot" = "1" ] || [ "$want_slot" = "2" ]; then
          echo s
          echo "$want_slot"
        fi
        echo e
      } > "$CASE_WORKDIR/restore_input.txt"
      cat "$CASE_WORKDIR/restore_input.txt" | "$TEST_BIN" >> "$restore_log" 2>&1 || true
      if ! snap_verify_file "1190.txt" "$VENDOR_FIXED_CFG"; then
        ok=0
      fi
    fi
  else
    case_log "no vendor file snapshot; slot restore only"
    {
      if [ "$want_slot" = "1" ] || [ "$want_slot" = "2" ]; then
        echo s
        echo "$want_slot"
      fi
      echo e
    } > "$CASE_WORKDIR/restore_input.txt"
    cat "$CASE_WORKDIR/restore_input.txt" | "$TEST_BIN" >> "$restore_log" 2>&1 || true
  fi

  cp "$restore_log" "$RESULT_DIR/logs/${CASE_ID}_restore.log" 2>/dev/null || true

  # 二次读确认：查当前槽位 == 测前快照
  if [ "$want_slot" = "1" ] || [ "$want_slot" = "2" ]; then
    now=$(vc_query_slot)
    case_log "restore verify slot: want=$want_slot now=$now"
    if [ "$now" != "$want_slot" ]; then
      case_log "slot restore confirm FAIL"
      ok=0
    else
      case_log "slot restore confirm OK"
    fi
  else
    case_log "ORIG_SLOT unknown; skip slot confirm"
  fi

  [ "$ok" -eq 1 ] && return 0 || return 1
}
