# -*- coding: utf-8 -*-
"""
将 hal_selfcheck/*/manifest.json 同步到 ATF「脚本列表」（POST /api/script）。

用法：
  python t2_atf_script_sync.py
  python t2_atf_script_sync.py --config t2_atf_config.json --dry-run

说明：
  - 只读复用 BMC config.json 的 atf/pc 账号
  - 不修改 BMC_ATF_auto-exec-controller 源码
  - 同步后可在 http://{atf}/ 设备页「脚本列表」看到对应项并勾选
"""

from __future__ import annotations

import argparse
import json
import os
import sys

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))


def _load_json(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def main() -> int:
    parser = argparse.ArgumentParser(description="同步 HAL selfcheck 到 ATF 脚本列表")
    parser.add_argument("--config", default=os.path.join(SCRIPT_DIR, "t2_atf_config.json"))
    parser.add_argument("--dry-run", action="store_true", help="只打印将同步的条目，不请求网络")
    args = parser.parse_args()

    if not os.path.isfile(args.config):
        print(f"[ERROR] 配置不存在: {args.config}")
        return 1

    t2 = _load_json(args.config)
    bmc_path = t2.get("bmc_config_path") or ""
    if not os.path.isabs(bmc_path):
        bmc_path = os.path.normpath(os.path.join(SCRIPT_DIR, bmc_path))
    if not os.path.isfile(bmc_path):
        print(f"[ERROR] BMC config 不存在: {bmc_path}")
        return 1
    bmc = _load_json(bmc_path)
    atf = bmc.get("atf") or {}
    pc = bmc.get("pc") or {}

    from t2_script_pool import collect_atf_script_entries
    entries = collect_atf_script_entries(SCRIPT_DIR)
    if not entries:
        print("[WARN] 未找到可同步的 hal_selfcheck manifest")
        return 2

    print(f"[INFO] 待同步 {len(entries)} 条到 ATF 脚本列表：")
    for e in entries:
        print(f"  - {e['script_name']} | {e['script_desc']} | selected={e['default_selected']}")

    if args.dry_run:
        print("[INFO] dry-run，结束")
        return 0

    try:
        import requests
    except ImportError:
        print("[ERROR] 需要 requests 包")
        return 3

    base = atf.get("base_url", "http://192.168.22.70:5000").rstrip("/")
    login_url = f"{base}{atf.get('login_path', '/api/login')}"
    script_url = f"{base}/api/script"
    body = {"user_id": atf.get("user_id", ""), "password": atf.get("password", "")}
    sess = requests.Session()
    r = sess.post(login_url, json=body, timeout=15)
    r.raise_for_status()
    token = r.json().get("token")
    headers = {"Authorization": f"Bearer {token}"}

    # 批量新增（字段名对齐 BMC sync_scripts）
    payload = []
    for e in entries:
        payload.append({
            "script_name": e["script_name"],
            "script_desc": e["script_desc"],
            "is_selected": bool(e.get("default_selected", True)),
            "pc_id": pc.get("pc_id", ""),
        })

    resp = sess.post(script_url, headers=headers, json=payload, timeout=30)
    print(f"[INFO] POST {script_url} -> {resp.status_code}")
    print(resp.text[:500])
    if resp.status_code not in (200, 201):
        # 部分平台要逐条 POST
        ok = 0
        for one in payload:
            rr = sess.post(script_url, headers=headers, json=one, timeout=20)
            print(f"  single {one['script_name']}: {rr.status_code}")
            if rr.status_code in (200, 201):
                ok += 1
        print(f"[INFO] 逐条成功 {ok}/{len(payload)}")
        return 0 if ok else 4
    print("[INFO] 同步完成。请到 ATF 设备页「脚本列表」查看/勾选。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
