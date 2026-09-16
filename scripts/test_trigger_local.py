#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
测试触发脚本 —— 模拟 BMC_ATF_auto-exec-controller 的 build_pigeon.py 通知 scripts 侧。

用本地路径模拟，跳过 OSS：
  python test_trigger_local.py --ip 127.0.0.1 --port 8007 --package "e:\\BMCSJB\\update.zip"

发给 t2_upgrade_entry.py 监听端的消息：
  {"package_path": "e:\\BMCSJB\\update.zip"}

监听端收到后：ACK -> 直接用本地包走 SocOtaUpgrade 升级 -> 落地报告/摘要。

如需走真 OSS 链路，改用 BMC 的 build_pigeon.py：
  python build_pigeon.py --ip <PC_IP> --port 8007 \
      --soc_version oss://... --parent_dir <dir>
"""

import argparse
import json
import socket
import sys


def main():
    parser = argparse.ArgumentParser(description="模拟 BMC 通知 scripts 升级（本地路径）")
    parser.add_argument("--ip", default="127.0.0.1", help="运行 t2_upgrade_entry.py 的 PC/IP（默认本机）")
    parser.add_argument("--port", type=int, default=8007, help="t2_upgrade_entry.py 监听端口（默认 8007）")
    parser.add_argument("--package", required=True, help="本地升级包路径")
    parser.add_argument("--soc_version", default="", help="可选：附带版本号，写入摘要便于追溯")
    parser.add_argument("--parent_dir", default="", help="可选：附带父目录名，写入摘要便于追溯")
    args = parser.parse_args()

    msg = {
        "package_path": args.package,
        "soc_version": args.soc_version,
        "parent_dir_name": args.parent_dir,
    }
    payload = json.dumps(msg).encode("utf-8")

    print(f"[INFO] 发送触发 -> {args.ip}:{args.port}")
    print(f"[INFO] 消息: {msg}")

    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(10)
        sock.connect((args.ip, args.port))
        sock.sendall(payload)
        resp = sock.recv(64).decode("utf-8")
        sock.close()
    except Exception as e:
        print(f"[ERROR] 连接/发送失败: {e}")
        return 1

    if resp == "ACK":
        print("[INFO] 对端已确认接收（ACK），升级任务已启动")
        return 0
    print(f"[ERROR] 对端响应异常: {resp}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
