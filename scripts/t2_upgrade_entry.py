#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
T2 ATF 升级入口（脚本驱动）

使用方式：
  1) 直接给定升级包路径：
       python t2_upgrade_entry.py --package "D:\\ota\\update.zip"
  2) 监听 TCP（可选并行邮箱），接收 build_pigeon / 邮件触发：
       python t2_upgrade_entry.py --listen
  3) 仅监听邮箱（IMAP）：
       python t2_upgrade_entry.py --listen-email
  4) 模拟邮件触发（不连 IMAP）：
       python t2_upgrade_entry.py --simulate-email --source "E:\\path\\update.zip"
       python t2_upgrade_entry.py --simulate-email --source "E:\\path\\update.zip" --fetch-only

升级由 scripts/SocOtaUpgrade/SocOtaUpgrade.exe 完成。
不修改 BMC_ATF_auto-exec-controller；oss/atf/pc 配置只读复用其 config.json。
"""

import argparse
import datetime
import email
import email.header
import glob
import html as html_lib
import imaplib
import json
import os
import poplib
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET

try:
    import requests
except ImportError:
    requests = None  # 仅在 OSS 下载模式需要

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))


# ---------------------------------------------------------------------------
# 配置加载
# ---------------------------------------------------------------------------
def load_t2_config(config_path: str) -> dict:
    with open(config_path, "r", encoding="utf-8") as f:
        return json.load(f)


def load_bmc_config(t2_cfg: dict) -> dict:
    """只读复用 BMC 的 config.json 中的 oss/atf/pc 配置，避免密钥重复。"""
    rel = t2_cfg.get("bmc_config_path", "../BMC_ATF_auto-exec-controller/config.json")
    path = os.path.normpath(os.path.join(SCRIPT_DIR, rel))
    if not os.path.isfile(path):
        print(f"[WARN] BMC config not found: {path}（仅 --package 直传模式可用）")
        return {}
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


# ---------------------------------------------------------------------------
# 工具
# ---------------------------------------------------------------------------
def resolve_exe(t2_cfg: dict) -> str:
    rel = t2_cfg.get("soc_ota_exe", "SocOtaUpgrade/SocOtaUpgrade.exe")
    candidates = [
        os.path.normpath(os.path.join(SCRIPT_DIR, rel)),
        os.path.normpath(os.path.join(SCRIPT_DIR, "SocOtaUpgrade.exe")),
        os.path.normpath(os.path.join(SCRIPT_DIR, "SocOtaUpgrade", "SocOtaUpgrade.exe")),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c
    raise FileNotFoundError(f"SocOtaUpgrade.exe 未找到，已尝试: {candidates}")


def find_latest_session_dir(exe_dir: str, since: float = 0) -> str:
    """SocOtaUpgrade 升级日志在 exe 同级 logs/<时间戳>/ 下。since>0 时只取该时间之后创建的会话。"""
    log_root = os.path.join(exe_dir, "logs")
    if not os.path.isdir(log_root):
        return ""
    dirs = sorted(
        (d for d in glob.glob(os.path.join(log_root, "*")) if os.path.isdir(d)),
        key=lambda p: os.path.getmtime(p),
        reverse=True,
    )
    if since > 0:
        # 允许 2s 时钟/创建延迟
        dirs = [d for d in dirs if os.path.getmtime(d) >= since - 2]
    return dirs[0] if dirs else ""


def parse_upgrade_result(session_dir: str) -> dict:
    """读取 version_verify.json / hal_status.json，汇总升级结果。"""
    result = {"session_dir": session_dir, "version_verify": None, "hal_status": None}
    if not session_dir or not os.path.isdir(session_dir):
        return result
    for name in ("version_verify.json", "hal_status.json"):
        fp = os.path.join(session_dir, name)
        if os.path.isfile(fp):
            try:
                with open(fp, "r", encoding="utf-8-sig") as f:
                    result[name.replace(".json", "")] = json.load(f)
            except Exception as e:
                result[name.replace(".json", "")] = {"_parse_error": str(e)}
    return result


# ---------------------------------------------------------------------------
# OSS 下载（复用 BMC network_client 的签名 URL 方式）
# ---------------------------------------------------------------------------
def generate_oss_url(oss_cfg: dict, soc_version: str, parent_dir: str, file_name: str):
    try:
        import oss2
    except ImportError:
        raise RuntimeError("缺少 oss2 包，请 pip install oss2（仅在 --listen/OSS 下载模式需要）")

    auth = oss2.Auth(oss_cfg["access_key_id"], oss_cfg["access_key_secret"])
    bucket = oss2.Bucket(auth, oss_cfg["endpoint"], oss_cfg["bucket_name"])
    oss_path = f"{soc_version}{parent_dir}/{file_name}"
    if not bucket.object_exists(oss_path):
        raise FileNotFoundError(f"OSS 对象不存在: {oss_path}")
    return bucket.sign_url("GET", oss_path, 3600)


def download_package(url: str, dest_dir: str, file_name: str) -> str:
    if requests is None:
        raise RuntimeError("缺少 requests 包，请 pip install requests")
    os.makedirs(dest_dir, exist_ok=True)
    dest = os.path.join(dest_dir, file_name)
    if os.path.exists(dest):
        os.remove(dest)
    print(f"[INFO] 下载升级包 -> {dest}")
    resp = requests.get(url, stream=True, timeout=600)
    resp.raise_for_status()
    with open(dest, "wb") as f:
        for chunk in resp.iter_content(chunk_size=8192):
            f.write(chunk)
    print(f"[INFO] 下载完成: {dest} ({os.path.getsize(dest)/1024/1024:.2f} MB)")
    return dest


# ---------------------------------------------------------------------------
# 升级执行
# ---------------------------------------------------------------------------
def _poll_progress(exe_dir: str, run_start: float, last: dict) -> tuple:
    """读取 exe 写出的 progress.json，返回 (最新阶段 dict, 会话目录)。"""
    log_root = os.path.join(exe_dir, "logs")
    if not os.path.isdir(log_root):
        return last, ""
    try:
        dirs = sorted(
            (d for d in glob.glob(os.path.join(log_root, "*")) if os.path.isdir(d)),
            key=lambda p: os.path.getmtime(p),
            reverse=True,
        )
    except Exception:
        return last, ""
    for d in dirs:
        if os.path.getmtime(d) < run_start - 2:
            break
        pp = os.path.join(d, "progress.json")
        if os.path.isfile(pp):
            try:
                with open(pp, "r", encoding="utf-8-sig") as f:
                    data = json.load(f)
                return data, d
            except Exception:
                continue
    return last, ""


def run_upgrade(t2_cfg: dict, package_path: str, report_mode: bool = False) -> tuple:
    """返回 (exit_code, session_dir)。"""
    exe = resolve_exe(t2_cfg)
    extra = list(t2_cfg.get("soc_ota_extra_args", []))
    workdir = t2_cfg.get("soc_ota_workdir") or os.path.dirname(exe)
    exe_dir = os.path.dirname(exe)

    # 监听触发时加 -report：升级+验证完成后弹窗说明，不直接退出
    if report_mode and "-report" not in [a.lower() for a in extra]:
        extra.append("-report")

    args = [exe] + extra + ["-package", package_path]
    print(f"[INFO] 调用 SocOtaUpgrade: {' '.join(args)}")
    print(f"[INFO] 工作目录: {workdir}")

    run_start = time.time()
    try:
        proc = subprocess.Popen(args, cwd=workdir)
    except KeyboardInterrupt:
        print("[WARN] 用户中断")
        return 130, ""

    last_stage = {"stage": "", "status": "", "message": ""}
    session_dir = ""
    print(f"[STAGE] stage=init status=running msg=已启动 SocOtaUpgrade")
    early_exit_code = None
    try:
        while proc.poll() is None:
            time.sleep(1)
            cur, sess = _poll_progress(exe_dir, run_start, last_stage)
            if sess:
                session_dir = sess
            if cur is not last_stage and (cur.get("stage") != last_stage.get("stage")
                                          or cur.get("status") != last_stage.get("status")):
                last_stage = cur
                print(f"[STAGE] stage={cur.get('stage','')} status={cur.get('status','')} "
                      f"elapsed={cur.get('elapsed_sec','?')}s msg={cur.get('message','')}")
                # 检测到终态（done/failed/cancelled）立即返回，不必等 exe 退出（exe 在 -report 模式下可能正弹窗）
                st = cur.get("stage", "")
                if st == "done" and cur.get("status") == "success":
                    early_exit_code = 0
                    print(f"[INFO] 检测到升级完成，立即进入上报流程（exe 可能仍弹窗，不阻塞）")
                    break
                if st in ("failed", "cancelled"):
                    early_exit_code = (2 if st == "cancelled" else 1)
                    print(f"[INFO] 检测到升级{('终止' if st=='cancelled' else '失败')}，立即进入上报流程（exe 可能仍弹窗，不阻塞）")
                    break
    except KeyboardInterrupt:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()
        print("[WARN] 用户中断")
        return 130, session_dir

    if early_exit_code is not None:
        # exe 可能仍在弹窗，留它在后台自行结束；不等待
        if not session_dir:
            session_dir = find_latest_session_dir(exe_dir, run_start)
        print(f"[INFO] SocOtaUpgrade 终态退出码: {early_exit_code}")
        return early_exit_code, session_dir

    exit_code = proc.returncode if proc.returncode is not None else 1
    # 退出后再读一次最终状态
    final, sess = _poll_progress(exe_dir, run_start, last_stage)
    if sess:
        session_dir = sess
    if final is not last_stage and final.get("stage"):
        print(f"[STAGE] stage={final.get('stage','')} status={final.get('status','')} "
              f"elapsed={final.get('elapsed_sec','?')}s msg={final.get('message','')}")
    if not session_dir:
        session_dir = find_latest_session_dir(exe_dir, run_start)
    print(f"[INFO] SocOtaUpgrade 退出码: {exit_code}")
    return exit_code, session_dir


# ---------------------------------------------------------------------------
# ATF 上传（仅监听触发时上报；不依赖 oss2，独立实现 ATF 协议）
# ---------------------------------------------------------------------------
class _AtfClient:
    """最小 ATF 客户端：登录/注册PC/上报状态/上传文件。不依赖 oss2。"""
    def __init__(self, atf_config: dict, pc_body: dict):
        base = atf_config.get("base_url", "http://192.168.22.70:5000")
        self.login_url = f"{base}{atf_config.get('login_path', '/api/login')}"
        self.pc_url = f"{base}{atf_config.get('pc_path', '/api/pc')}"
        self.upload_url = f"{base}{atf_config.get('upload_path', '/api/file')}"
        self.status_url = f"{base}/api/status"
        self.body = {"user_id": atf_config.get("user_id", "jiaojing"), "password": atf_config.get("password", "123456")}
        self.pc_body = pc_body
        self.token = ""
        self.headers = {}
        self.session = requests.Session()

    def refresh_token(self):
        r = self.session.post(self.login_url, json=self.body, timeout=10)
        r.raise_for_status()
        self.token = r.json()["token"]
        self.headers = {"Authorization": f"Bearer {self.token}"}

    def _req(self, method, url, json_data=None, form_data=None, files=None, timeout=15):
        if not self.token:
            self.refresh_token()
        if method == "GET":
            resp = self.session.get(url, headers=self.headers, timeout=timeout)
        elif files or form_data:
            resp = self.session.post(url, headers=self.headers, data=form_data, files=files, timeout=timeout)
        else:
            resp = self.session.post(url, headers=self.headers, json=json_data, timeout=timeout)
        if resp.status_code == 401:
            self.refresh_token()
            if self.token:
                if files or form_data:
                    resp = self.session.post(url, headers=self.headers, data=form_data, files=files, timeout=timeout)
                else:
                    resp = self.session.post(url, headers=self.headers, json=json_data, timeout=timeout)
        return resp

    def register_pc(self) -> bool:
        try:
            resp = self._req("POST", self.pc_url, json_data=self.pc_body, timeout=10)
            if resp.status_code in (201, 200):
                return True
            if resp.status_code == 400 and "already exists" in resp.text:
                return True
            print(f"[WARN] 注册PC失败: {resp.status_code} {resp.text[:150]}")
            return False
        except Exception as e:
            print(f"[WARN] 注册PC异常: {e}")
            return False

    def report_status(self, pc_id: str, status: str, report_type: int = 1) -> bool:
        try:
            resp = self._req("POST", self.status_url, json_data={"pc_id": pc_id, "status": status, "report_type": report_type}, timeout=10)
            if resp.status_code in (201, 200):
                return True
            print(f"[WARN] 状态上报失败: {resp.status_code} {resp.text[:150]}")
            return False
        except Exception as e:
            print(f"[WARN] 状态上报异常: {e}")
            return False

    def upload_files(self, pc_id: str, files: list) -> bool:
        # files: [("file", (name, bytes)), ...]
        try:
            resp = self._req("POST", self.upload_url, form_data={"pc_id": pc_id}, files=files, timeout=120)
            if resp.status_code in (201, 200):
                return True
            print(f"[WARN] 文件上传失败: {resp.status_code} {resp.text[:150]}")
            return False
        except Exception as e:
            print(f"[WARN] 文件上传异常: {e}")
            return False

    def upload_detect_dir(self, pc_id: str, detect_dir: str) -> bool:
        """
        上传 detect/ 目录（对齐 BMC：multipart 文件名形如 detect/detect_result.html）。
        ATF 会落到 /uploads/{PC名}/{日期}/{时间}/detect/...
        """
        if not detect_dir or not os.path.isdir(detect_dir):
            print("[WARN] detect 目录不存在，跳过报告上传")
            return False
        files = []
        for root, _dirs, names in os.walk(detect_dir):
            for name in names:
                full = os.path.join(root, name)
                rel = os.path.relpath(full, detect_dir).replace("\\", "/")
                atf_name = f"detect/{rel}"
                try:
                    with open(full, "rb") as f:
                        files.append(("file", (atf_name, f.read())))
                except Exception as e:
                    print(f"[WARN] 读取 {atf_name} 失败: {e}")
        if not files:
            print("[WARN] detect 目录为空")
            return False
        print(f"[INFO] 上传测试报告目录: {detect_dir}（{len(files)} 个文件）")
        ok = self.upload_files(pc_id, files)
        if ok:
            print("[INFO] 测试报告已上传（可在 ATF「测试报告」中查看 detect_result.html）")
        return ok


def upload_to_atf(t2_cfg: dict, bmc_cfg: dict, result: dict, package_path: str) -> bool:
    """监听触发时：登录 ATF、注册 PC、上传升级/验证产物、回报状态（成功/失败都上报）。"""
    if not t2_cfg.get("atf_upload", {}).get("enabled", False):
        print("[INFO] ATF 上传未启用（atf_upload.enabled=false），跳过")
        return False

    atf_config = bmc_cfg.get("atf", {})
    pc_config = bmc_cfg.get("pc", {})
    if not atf_config or not pc_config.get("pc_id"):
        print("[WARN] 缺少 atf/pc 配置，无法上报")
        return False
    if requests is None:
        print("[ERROR] 缺少 requests 包，无法上报 ATF")
        return False

    pc_id = _resolve_pc_id(bmc_cfg) or pc_config.get("pc_id", "")
    pc_body = {"pc_id": pc_id, "name": pc_config.get("name", ""), "alias": pc_config.get("alias", ""), "ip_address": pc_config.get("ip_address", "")}

    try:
        client = _AtfClient(atf_config, pc_body)
        client.register_pc()
    except Exception as e:
        print(f"[ERROR] ATF 连接/登录失败: {e}")
        return False

    exit_code = result.get("exit_code", 1)
    session_dir = result.get("session_dir", "")
    overall_ok = (exit_code == 0)

    # 读取 progress.json 拿到失败阶段/原因，写进状态文本
    fail_reason = ""
    progress_path = os.path.join(session_dir, "progress.json") if session_dir else ""
    if progress_path and os.path.isfile(progress_path):
        try:
            with open(progress_path, "r", encoding="utf-8-sig") as f:
                prog = json.load(f)
            result["progress"] = prog
            if not overall_ok:
                fail_reason = f"{prog.get('stage','')}:{prog.get('message','')}"
        except Exception as e:
            print(f"[WARN] 读取 progress.json 失败: {e}")

    if overall_ok:
        ur = result.get("unified_report") or {}
        post = (ur.get("post_test_result") or {})
        sp = (post.get("script_pool") or {})
        hal = result.get("hal_status") or {}
        if hal.get("Passed"):
            pc = hal.get("PassCount", 0)
            fc = hal.get("FailCount", 0)
            sc = hal.get("SkipCount", 0)
            status_text = f"T2 升级成功，HAL检查Pass({pc}Pass/{fc}Fail/{sc}Skip)"
        elif hal:
            status_text = (
                f"T2 升级成功但HAL检查Fail(Pass={hal.get('PassCount', 0)}, "
                f"Fail={hal.get('FailCount', 0)})"
            )
        else:
            status_text = "T2 升级成功（HAL报告未生成）"
        if sp.get("enabled"):
            status_text += (
                f"；脚本池={'Pass' if sp.get('passed') else 'Fail'}"
                f"(P={sp.get('pass_count', 0)}/F={sp.get('fail_count', 0)}/S={sp.get('skip_count', 0)})"
            )
    else:
        status_text = f"T2 升级失败(退出码={exit_code}, 原因={fail_reason})" if fail_reason else f"T2 升级失败(退出码={exit_code})"

    # 上传 session 目录下的关键产物（含 progress.json）
    uploaded = 0
    files = []
    if session_dir and os.path.isdir(session_dir):
        for name in ("progress.json", "version_verify.json", "hal_status.json",
                     "t2_task_summary.json", "t2_unified_report.json", "script_results.json"):
            fp = os.path.join(session_dir, name)
            if os.path.isfile(fp):
                try:
                    with open(fp, "rb") as f:
                        files.append(("file", (name, f.read())))
                    uploaded += 1
                except Exception as e:
                    print(f"[WARN] 读取 {name} 失败: {e}")
    if files:
        if client.upload_files(pc_id, files):
            print(f"[INFO] ATF 文件上传成功（{uploaded} 个）")

    # 上传脚本池 detect 报告（对齐平台「测试报告」）
    detect_dir = ""
    sp = result.get("script_pool") or {}
    detect_dir = sp.get("detect_dir") or ""
    if not detect_dir and session_dir:
        cand = os.path.join(session_dir, "detect")
        if os.path.isdir(cand) and os.path.isfile(os.path.join(cand, "detect_result.html")):
            detect_dir = cand
    if detect_dir:
        try:
            client.upload_detect_dir(pc_id, detect_dir)
        except Exception as e:
            print(f"[WARN] detect 报告上传异常: {e}")

    # 回报状态（失败也上报）
    if client.report_status(pc_id, status_text, report_type=1):
        print(f"[INFO] ATF 状态已上报: {status_text}")
    return True


# ---------------------------------------------------------------------------
# 单次任务编排
# ---------------------------------------------------------------------------
def build_unified_task_report(result: dict, package_path: str = "") -> dict:
    """
    统一上报 Schema（阶段 09）：
      upgrade_version / upgrade_result / post_test_result / errors / trigger_meta
    """
    raw_exit = result.get("exit_code", 1)
    exit_code = 1 if raw_exit is None else int(raw_exit)
    vv = result.get("version_verify") or {}
    hs = result.get("hal_status") or {}
    sp = result.get("script_pool") or {}
    prog = result.get("progress") or {}

    upgrade_ok = exit_code == 0
    hal_ok = True if not hs else bool(hs.get("Passed", True))
    scripts_ok = True if not sp or not sp.get("enabled") else bool(sp.get("Passed", True))

    errors = []
    if not upgrade_ok:
        errors.append({
            "code": f"UPGRADE_EXIT_{exit_code}",
            "stage": prog.get("stage") or "upgrade",
            "message": prog.get("message") or f"升级退出码={exit_code}",
        })
    if hs and not hs.get("Passed", True):
        errors.append({
            "code": "HAL_L0_FAIL",
            "stage": "hal_check",
            "message": f"HAL FailCount={hs.get('FailCount', 0)}",
        })
    if sp and sp.get("enabled") and not sp.get("Passed", True):
        errors.append({
            "code": "SCRIPT_POOL_FAIL",
            "stage": "script_pool",
            "message": f"脚本池 FailCount={sp.get('FailCount', 0)}",
        })

    overall = "success" if (upgrade_ok and hal_ok and scripts_ok) else "failed"
    if exit_code == 2:
        overall = "cancelled"

    return {
        "schema_version": "1.0",
        "upgrade_version": {
            "soc_version": result.get("soc_version", ""),
            "parent_dir": result.get("parent_dir", ""),
            "package_path": package_path or result.get("package_path", ""),
            "version_verify": vv,
        },
        "upgrade_result": {
            "status": "success" if upgrade_ok else overall,
            "exit_code": exit_code,
            "progress": prog,
            "session_dir": result.get("session_dir", ""),
        },
        "post_test_result": {
            "hal_l0": {
                "passed": hs.get("Passed") if hs else None,
                "pass_count": hs.get("PassCount"),
                "fail_count": hs.get("FailCount"),
                "skip_count": hs.get("SkipCount"),
            },
            "script_pool": {
                "enabled": bool(sp.get("enabled")),
                "passed": sp.get("Passed") if sp else None,
                "pass_count": sp.get("PassCount"),
                "fail_count": sp.get("FailCount"),
                "skip_count": sp.get("SkipCount"),
                "items": sp.get("Items") or [],
            },
            "overall_passed": bool(hal_ok and scripts_ok and upgrade_ok),
        },
        "errors": errors,
        "trigger_meta": {
            "triggered_by_listen": bool(result.get("triggered_by_listen")),
            "package_path": package_path or result.get("package_path", ""),
        },
    }




def cleanup_upgrade_package(t2_cfg: dict, package_path: str) -> None:
    """测试完成后默认删除托管目录内的升级包；路径/版本仅保留在报告中。"""
    if t2_cfg.get("keep_downloaded_package", False):
        print("[INFO] keep_downloaded_package=true，保留升级包文件")
        return
    if not package_path:
        return
    path = os.path.abspath(package_path)
    if not os.path.exists(path):
        return

    managed = []
    for key in ("download_dir",):
        rel = t2_cfg.get(key) or ""
        if rel:
            managed.append(os.path.abspath(os.path.join(SCRIPT_DIR, rel)))
    email_cfg = t2_cfg.get("email") or {}
    save_dir = email_cfg.get("package_save_dir") or ""
    if save_dir:
        managed.append(os.path.abspath(save_dir))

    def _under(root: str, target: str) -> bool:
        try:
            return os.path.commonpath([root, target]) == root
        except Exception:
            return False

    if not any(_under(root, path) for root in managed if root):
        print(f"[INFO] 升级包不在托管下载目录，跳过删除: {path}")
        return
    try:
        if os.path.isdir(path):
            import shutil
            shutil.rmtree(path, ignore_errors=True)
        else:
            os.remove(path)
        print(f"[INFO] 已删除升级包文件（报告中仍保留路径/版本）: {path}")
        # 顺带清理同目录解压残留（同名前缀）
        parent = os.path.dirname(path)
        stem = os.path.splitext(os.path.basename(path))[0]
        for name in os.listdir(parent) if os.path.isdir(parent) else []:
            if name.startswith(stem) and name != os.path.basename(path):
                cand = os.path.join(parent, name)
                if os.path.isdir(cand) and ("extract" in name.lower() or name.endswith("_unzip")):
                    import shutil
                    shutil.rmtree(cand, ignore_errors=True)
    except Exception as e:
        print(f"[WARN] 删除升级包失败: {e}")

def execute_task(t2_cfg: dict, bmc_cfg: dict, package_path: str,
                 soc_version: str = "", parent_dir: str = "",
                 triggered_by_listen: bool = False) -> int:
    """完整流程：准备升级包 -> 升级 -> 脚本池 -> 统一摘要 -> （监听触发时）上传 ATF。"""
    try:
        package_path = prepare_ota_package_for_upgrade(os.path.abspath(package_path), t2_cfg)
    except Exception as e:
        print(f"[ERROR] 准备升级包失败: {e}")
        return 3

    if not os.path.isfile(package_path) and not os.path.isdir(package_path):
        print(f"[ERROR] 升级包不存在: {package_path}")
        return 2

    exit_code = 1
    session_dir = ""
    result = {}
    try:
        print(f"[INFO] 开始完整 SOC 升级: {package_path}")
        exit_code, session_dir = run_upgrade(t2_cfg, package_path, report_mode=triggered_by_listen)
    except Exception as e:
        print(f"[ERROR] run_upgrade 异常: {e}")
        exit_code = 1

    try:
        if not session_dir:
            exe = resolve_exe(t2_cfg)
            session_dir = find_latest_session_dir(os.path.dirname(exe))
        if exit_code == 0 and session_dir:
            for _ in range(20):
                if os.path.isfile(os.path.join(session_dir, "hal_status.json")):
                    break
                time.sleep(0.25)
        result = parse_upgrade_result(session_dir)
    except Exception as e:
        print(f"[WARN] 解析升级结果异常: {e}")

    result["exit_code"] = exit_code
    result["soc_version"] = soc_version
    result["parent_dir"] = parent_dir
    result["package_path"] = package_path
    result["session_dir"] = session_dir or ""
    result["triggered_by_listen"] = triggered_by_listen

    if session_dir:
        pp = os.path.join(session_dir, "progress.json")
        if os.path.isfile(pp):
            try:
                with open(pp, "r", encoding="utf-8-sig") as f:
                    result["progress"] = json.load(f)
            except Exception:
                pass

    print(f"[INFO] 会话目录: {session_dir}")
    print(f"[INFO] 退出码: {exit_code}")
    if result.get("version_verify"):
        print(f"[INFO] 版本校验: {result['version_verify'].get('Passed', result['version_verify'])}")
    if result.get("hal_status"):
        print(f"[INFO] HAL 状态: {result['hal_status'].get('Passed', result['hal_status'])}")

    pool_cfg = t2_cfg.get("script_pool") or {}
    run_pool = bool(pool_cfg.get("enabled", False))
    run_when = (pool_cfg.get("run_when") or "on_upgrade_success").strip().lower()
    if run_pool and (run_when == "always" or (run_when == "on_upgrade_success" and exit_code == 0)):
        try:
            from t2_script_pool import run_script_pool
            pool_path = pool_cfg.get("config_path") or os.path.join(SCRIPT_DIR, "t2_script_pool.json")
            if not os.path.isabs(pool_path):
                pool_path = os.path.normpath(os.path.join(SCRIPT_DIR, pool_path))
            pc_cfg = bmc_cfg.get("pc") or {}
            print("[INFO] ===== 流水线：脚本池自测（HAL 模块 + 报告） =====")
            sp = run_script_pool(
                SCRIPT_DIR,
                pool_path if os.path.isabs(pool_path) else os.path.join(SCRIPT_DIR, pool_path),
                session_dir=session_dir or SCRIPT_DIR,
                pc_id=_resolve_pc_id(bmc_cfg) or "",
                pc_name=(bmc_cfg.get("pc") or {}).get("name", ""),
                soc_version=soc_version or result.get("soc_version", ""),
                build_detect_report=bool(pool_cfg.get("build_detect_report", True)),
                parent_dir_name=parent_dir or result.get("parent_dir", ""),
                package_path=package_path,
                version_verify=result.get("version_verify"),
                hal_status=result.get("hal_status"),
                progress=result.get("progress"),
            )
            result["script_pool"] = sp
            if pool_cfg.get("fail_task_on_script_fail") and sp.get("enabled") and not sp.get("Passed", True):
                print("[WARN] 脚本池失败且 fail_task_on_script_fail=true，退出码=4")
                exit_code = 4
                result["exit_code"] = exit_code
        except Exception as e:
            print(f"[WARN] 脚本池执行异常: {e}")
            result["script_pool"] = {"enabled": True, "Passed": False, "error": str(e)}

    unified = build_unified_task_report(result, package_path)
    result["unified_report"] = unified

    summary_path = os.path.join(session_dir or SCRIPT_DIR, "t2_task_summary.json")
    try:
        with open(summary_path, "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
        print(f"[INFO] 任务摘要: {summary_path}")
        unified_path = os.path.join(session_dir or SCRIPT_DIR, "t2_unified_report.json")
        with open(unified_path, "w", encoding="utf-8") as f:
            json.dump(unified, f, ensure_ascii=False, indent=2)
        print(f"[INFO] 统一报告: {unified_path}")
    except Exception as e:
        print(f"[WARN] 写摘要失败: {e}")

    # 生成/刷新 BMC 风格 HTML 报告（含包路径与版本）
    try:
        from t2_detect_report import build_detect_dir
        mods = (result.get("script_pool") or {}).get("module_results") or []
        detect_dir = build_detect_dir(
            session_dir or SCRIPT_DIR,
            mods,
            pc_id=_resolve_pc_id(bmc_cfg) or "",
            pc_name=(bmc_cfg.get("pc") or {}).get("name", ""),
            soc_version=soc_version or result.get("soc_version", ""),
            parent_dir_name=parent_dir or result.get("parent_dir", ""),
            version_verify=result.get("version_verify"),
            hal_status=result.get("hal_status"),
            script_pool=result.get("script_pool"),
            package_path=package_path,
            progress=result.get("progress"),
        )
        if isinstance(result.get("script_pool"), dict):
            result["script_pool"]["detect_dir"] = detect_dir
        print(f"[INFO] BMC风格测试报告: {os.path.join(detect_dir, 'detect_result.html')}")
    except Exception as e:
        print(f"[WARN] 刷新 detect_result.html 失败: {e}")

    if triggered_by_listen:
        try:
            upload_to_atf(t2_cfg, bmc_cfg, result, package_path)
        except Exception as e:
            print(f"[WARN] ATF 上传异常: {e}")

    try:
        cleanup_upgrade_package(t2_cfg, package_path)
    except Exception as e:
        print(f"[WARN] 清理升级包异常: {e}")

    return exit_code


def handle_trigger(t2_cfg: dict, bmc_cfg: dict, soc_version: str, parent_dir: str) -> int:
    """build_pigeon 触发路径：OSS 下载 + 升级。"""
    oss_cfg = bmc_cfg.get("oss", {})
    if not oss_cfg:
        print("[ERROR] 缺少 oss 配置（BMC config.json 不可用），无法下载")
        return 3

    base_path = oss_cfg.get("base_path", "oss://hangshengbuket/")
    sv = soc_version.replace(base_path, "")
    file_name = t2_cfg.get("upgrade_file_name", bmc_cfg.get("upgrade", {}).get("upgrade_file_name", "ota.zip"))

    print(f"[INFO] 触发: soc_version={sv}, parent_dir={parent_dir}, file={file_name}")
    try:
        url = generate_oss_url(oss_cfg, sv, parent_dir, file_name)
    except Exception as e:
        print(f"[ERROR] 生成 OSS 下载链接失败: {e}")
        return 3

    dest_dir = os.path.normpath(os.path.join(SCRIPT_DIR, t2_cfg.get("download_dir", "./t2_downloads")))
    try:
        package_path = download_package(url, dest_dir, file_name)
    except Exception as e:
        print(f"[ERROR] 下载升级包失败: {e}")
        return 3

    return execute_task(t2_cfg, bmc_cfg, package_path, soc_version=sv, parent_dir=parent_dir,
                        triggered_by_listen=True)


# ---------------------------------------------------------------------------
# 空闲心跳（让 ATF 平台看到设备在线）
# ---------------------------------------------------------------------------
def _resolve_pc_id(bmc_cfg: dict) -> str:
    """解析运行时使用的 pc_id（use_json_config=false 时取 BIOS UUID，与 BMC 一致）。"""
    pc_config = bmc_cfg.get("pc", {})
    if not pc_config.get("use_json_config", False):
        bmc_dir = os.path.dirname(os.path.normpath(os.path.join(SCRIPT_DIR, "..", "BMC_ATF_auto-exec-controller", "config.json")))
        # 复用 BMC 的 get_bios_uuid（wmic csproduct get uuid）
        try:
            if bmc_dir not in sys.path:
                sys.path.insert(0, bmc_dir)
            from get_bios_uuid import get_bios_uuid
            bid = get_bios_uuid()
            if bid:
                return bid.upper()
        except Exception:
            pass
        return pc_config.get("pc_id", "")
    return pc_config.get("pc_id", "")


def _build_atf_client(bmc_cfg: dict, t2_cfg: dict):
    """构造一个 _AtfClient 用于心跳/上报，失败返回 None。"""
    atf_config = bmc_cfg.get("atf", {})
    pc_config = bmc_cfg.get("pc", {})
    if not atf_config or not pc_config.get("pc_id") or requests is None:
        return None
    pc_id = _resolve_pc_id(bmc_cfg)
    pc_body = {"pc_id": pc_id, "name": pc_config.get("name", ""), "alias": pc_config.get("alias", ""), "ip_address": pc_config.get("ip_address", "")}
    try:
        return _AtfClient(atf_config, pc_body)
    except Exception:
        return None


def idle_heartbeat_loop(bmc_cfg: dict, t2_cfg: dict, pause_event: threading.Event, stop_event: threading.Event, interval_sec: int = 300):
    """后台线程：每 interval_sec 上报一次'空闲'状态（report_type=0），任务期间暂停。"""
    if not t2_cfg.get("atf_upload", {}).get("enabled", False):
        return
    pc_id = _resolve_pc_id(bmc_cfg)
    if not pc_id:
        return
    print(f"[INFO] 空闲心跳已启动（每 {interval_sec}s 上报一次空闲状态，pc_id={pc_id}）")
    while not stop_event.is_set():
        if not pause_event.is_set():
            try:
                client = _build_atf_client(bmc_cfg, t2_cfg)
                if client is not None:
                    client.report_status(pc_id, "空闲", report_type=0)
            except Exception as e:
                print(f"[WARN] 空闲心跳异常: {e}")
        stop_event.wait(interval_sec)


# ---------------------------------------------------------------------------
# 邮箱监听（IMAP 轮询，与 TCP 并行）
# ---------------------------------------------------------------------------
def _decode_mime_header(value: str) -> str:
    if not value:
        return ""
    parts = email.header.decode_header(value)
    out = []
    for frag, enc in parts:
        if isinstance(frag, bytes):
            out.append(frag.decode(enc or "utf-8", errors="replace"))
        else:
            out.append(frag)
    return "".join(out)


def _extract_email_text(msg) -> str:
    """提取邮件主题 + 纯文本/HTML 正文（去标签）。"""
    chunks = []
    subj = _decode_mime_header(msg.get("Subject", ""))
    if subj:
        chunks.append(subj)
    if msg.is_multipart():
        for part in msg.walk():
            ctype = part.get_content_type()
            if ctype not in ("text/plain", "text/html"):
                continue
            try:
                payload = part.get_payload(decode=True) or b""
                charset = part.get_content_charset() or "utf-8"
                text = payload.decode(charset, errors="replace")
            except Exception:
                continue
            if ctype == "text/html":
                text = re.sub(r"<[^>]+>", " ", text)
            chunks.append(text)
    else:
        try:
            payload = msg.get_payload(decode=True) or b""
            charset = msg.get_content_charset() or "utf-8"
            chunks.append(payload.decode(charset, errors="replace"))
        except Exception:
            pass
    return "\n".join(chunks)


def parse_upgrade_trigger_from_text(text: str) -> dict:
    """
    从邮件文本解析升级包地址 / OSS 触发参数。
    返回字段之一：
      - package_url: 本地路径 / UNC / http(s) URL
      - package_path: 兼容旧字段（等同 package_url）
      - soc_version + parent_dir_name: OSS 路径组件
    """
    if not text:
        return {}

    def _pack(url="", sv="", pd=""):
        url = (url or "").strip().strip('"').strip("'")
        if not url and not (sv and pd):
            return {}
        out = {}
        if url:
            out["package_url"] = url
            out["package_path"] = url  # 兼容
        if sv:
            out["soc_version"] = sv
        if pd:
            out["parent_dir_name"] = pd
        return out

    # 1) JSON
    for m in re.finditer(r"\{[^{}]{8,1200}\}", text, re.S):
        try:
            obj = json.loads(m.group(0))
        except Exception:
            continue
        if not isinstance(obj, dict):
            continue
        url = (obj.get("package_url") or obj.get("package_path") or obj.get("url")
               or obj.get("ota_url") or obj.get("download_url") or "")
        url = str(url).strip() if url else ""
        sv = str(obj.get("soc_version", "")).strip()
        pd = str(obj.get("parent_dir_name", "")).strip()
        got = _pack(url, sv, pd)
        if got:
            return got

    # 2) 键值对
    url_m = re.search(
        r"(?:package_url|package_path|ota_url|download_url|url)\s*[:=]\s*([^\s\"'<>]+)",
        text, re.I)
    sv = re.search(r"soc_version\s*[:=]\s*([^\s\"'<>]+)", text, re.I)
    pd = re.search(r"parent_dir(?:_name)?\s*[:=]\s*([^\s\"'<>]+)", text, re.I)
    if url_m:
        return _pack(
            url_m.group(1),
            sv.group(1) if sv else "",
            pd.group(1) if pd else "",
        )
    if sv and pd:
        return _pack("", sv.group(1), pd.group(1))

    # 3) 直接匹配路径 / URL（支持 zip/7z/rar 等）
    arch = r"(?:zip|7z|rar|bin|img|tar(?:\.gz)?|tgz)"
    # http(s)://host/...archive
    http_m = re.search(rf"(https?://[^\s\"'<>]+\.{arch})", text, re.I)
    if http_m:
        return _pack(http_m.group(1))
    # http(s)://IP/...（无扩展名也接受）
    http_any = re.search(r"(https?://\d{1,3}(?:\.\d{1,3}){3}[^\s\"'<>]*)", text, re.I)
    if http_any:
        return _pack(http_any.group(1))
    # UNC: \\IP\share\... 或 //IP/share/...（路径可含中文）
    unc_m = re.search(rf"((?:\\\\|//)[^\s\"'<>]+\.{arch})", text, re.I)
    if unc_m:
        u = unc_m.group(1)
        if u.startswith("//"):
            u = "\\\\" + u[2:].replace("/", "\\")
        return _pack(u)
    # Windows 本地盘符
    local_m = re.search(rf"([A-Za-z]:\\[^\s\"'<>]+\.{arch})", text, re.I)
    if local_m:
        return _pack(local_m.group(1))
    return {}


def _mail_match_keyword(subject: str, body: str, keyword: str) -> bool:
    """主题或正文任一包含关键字即匹配（很多邮件把 T2_OTA 写在正文里）。"""
    kw = (keyword or "").strip()
    if not kw:
        return True
    blob = f"{subject or ''}\n{body or ''}"
    return kw.lower() in blob.lower()


def _parse_trigger_from_mail(subject: str, body: str) -> dict:
    """主题+正文一起解析升级包地址。"""
    return parse_upgrade_trigger_from_text(f"{subject or ''}\n{body or ''}")


def _resolve_package_save_dir(t2_cfg: dict) -> str:
    """邮箱触发时的本地保存目录：优先 email.package_save_dir，否则 download_dir。"""
    email_cfg = t2_cfg.get("email") or {}
    rel = (email_cfg.get("package_save_dir")
           or t2_cfg.get("download_dir")
           or "./t2_downloads")
    if os.path.isabs(rel):
        return os.path.normpath(rel)
    return os.path.normpath(os.path.join(SCRIPT_DIR, rel))


def _guess_file_name_from_source(source: str, default_name: str = "update.zip") -> str:
    try:
        from urllib.parse import urlparse, unquote
        if re.match(r"^https?://", source, re.I):
            path = unquote(urlparse(source).path or "")
            base = os.path.basename(path)
            if base:
                return base
        base = os.path.basename(source.rstrip("/\\"))
        if base:
            return base
    except Exception:
        pass
    return default_name


def _find_7z_exe(t2_cfg: dict = None) -> str:
    cfg = (t2_cfg or {}).get("email") or {}
    configured = (cfg.get("seven_zip_exe") or (t2_cfg or {}).get("seven_zip_exe") or "").strip()
    candidates = [
        configured,
        r"C:\Program Files\7-Zip\7z.exe",
        r"C:\Program Files (x86)\7-Zip\7z.exe",
        shutil.which("7z") or "",
        shutil.which("7za") or "",
    ]
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    return ""


def _is_payload_dir(path: str) -> bool:
    return (os.path.isdir(path)
            and os.path.isfile(os.path.join(path, "payload.bin"))
            and os.path.isfile(os.path.join(path, "payload_properties.txt")))


def _zip_looks_like_ota(zip_path: str) -> bool:
    try:
        import zipfile
        with zipfile.ZipFile(zip_path, "r") as zf:
            names = {os.path.basename(n).lower() for n in zf.namelist()}
            return "payload.bin" in names and "payload_properties.txt" in names
    except Exception:
        return False


def _find_ota_package_under(root: str) -> str:
    """在解压目录中查找 SocOtaUpgrade 可用的 .zip 或 payload 目录。"""
    if not root or not os.path.isdir(root):
        return ""
    if _is_payload_dir(root):
        return os.path.abspath(root)

    preferred_names = ("update.zip", "ota.zip", "update_usb.zip")
    for name in preferred_names:
        for dirpath, _dirs, files in os.walk(root):
            if name in files:
                cand = os.path.join(dirpath, name)
                if _zip_looks_like_ota(cand) or name == "update.zip":
                    return os.path.abspath(cand)

    # 任意含 payload 的 zip
    zip_candidates = []
    for dirpath, _dirs, files in os.walk(root):
        if _is_payload_dir(dirpath):
            return os.path.abspath(dirpath)
        for f in files:
            if f.lower().endswith(".zip"):
                zip_candidates.append(os.path.join(dirpath, f))
    for cand in sorted(zip_candidates, key=lambda p: os.path.getsize(p), reverse=True):
        if _zip_looks_like_ota(cand):
            return os.path.abspath(cand)

    # 回退：最大的 zip
    if zip_candidates:
        best = max(zip_candidates, key=lambda p: os.path.getsize(p))
        print(f"[WARN] 未校验到 payload.bin，回退使用: {best}")
        return os.path.abspath(best)
    return ""


def prepare_ota_package_for_upgrade(local_path: str, t2_cfg: dict = None, _depth: int = 0) -> str:
    """
    将邮件复制下来的包整理为 SocOtaUpgrade 可直接升级的路径：
      - 已是 .zip / payload 目录 → 原样返回
      - .7z/.rar → 用 7-Zip 解压后查找 update.zip / payload 目录
      - 支持嵌套周包（默认最多递归 3 层）
    """
    path = os.path.abspath(local_path)
    if not os.path.exists(path):
        raise FileNotFoundError(f"升级包不存在: {path}")
    if _depth > 3:
        raise RuntimeError(f"嵌套解压超过深度限制(3): {path}")

    if os.path.isdir(path):
        if _is_payload_dir(path):
            return path
        found = _find_ota_package_under(path)
        if found:
            fl = found.lower()
            if (fl.endswith(".7z") or fl.endswith(".rar")) and _depth < 3:
                print(f"[INFO] 目录内命中嵌套压缩包，继续解压: {found}")
                return prepare_ota_package_for_upgrade(found, t2_cfg, _depth + 1)
            return found
        raise FileNotFoundError(f"目录内未找到可用 OTA 包（需 update.zip 或 payload.bin）: {path}")

    lower = path.lower()
    if lower.endswith(".zip"):
        return path

    if not (lower.endswith(".7z") or lower.endswith(".rar")):
        raise ValueError(f"不支持的升级包格式（需 .zip/.7z 或 payload 目录）: {path}")

    seven = _find_7z_exe(t2_cfg)
    if not seven:
        raise RuntimeError(
            "检测到 .7z 升级包，但未找到 7z.exe。请安装 7-Zip 或在配置中设置 seven_zip_exe。"
        )

    extract_root = path + "_extracted"
    if os.path.isdir(extract_root):
        # 已解压过则直接复用
        found = _find_ota_package_under(extract_root)
        if found:
            fl = found.lower()
            if (fl.endswith(".7z") or fl.endswith(".rar")) and _depth < 3:
                print(f"[INFO] 复用目录内嵌套压缩包，继续解压: {found}")
                return prepare_ota_package_for_upgrade(found, t2_cfg, _depth + 1)
            print(f"[INFO] 复用已解压目录中的 OTA 包: {found}")
            return found
    else:
        os.makedirs(extract_root, exist_ok=True)

    print(f"[INFO] 解压升级包(.7z): {path}")
    print(f"[INFO] 解压目录: {extract_root}")
    # 先尝试只解出常见 OTA 文件，失败再全量解压
    selective = [
        seven, "x", path, f"-o{extract_root}", "-y",
        "-ir!update.zip", "-ir!ota.zip", "-ir!payload.bin", "-ir!payload_properties.txt",
    ]
    cp = subprocess.run(selective, capture_output=True, text=True, timeout=3600)
    found = _find_ota_package_under(extract_root)
    if not found:
        print("[INFO] 选择性解压未找到 OTA 文件，改为全量解压（可能较久）...")
        cp = subprocess.run(
            [seven, "x", path, f"-o{extract_root}", "-y"],
            capture_output=True, text=True, timeout=7200,
        )
        if cp.returncode != 0:
            err = (cp.stderr or cp.stdout or "").strip()
            raise RuntimeError(f"7z 解压失败 (code={cp.returncode}): {err[:500]}")
        found = _find_ota_package_under(extract_root)

    if not found:
        # 解压目录内可能只有嵌套周包
        nested_archives = []
        for dirpath, _dirs, files in os.walk(extract_root):
            for fn in files:
                low = fn.lower()
                if low.endswith(".7z") or low.endswith(".rar"):
                    nested_archives.append(os.path.join(dirpath, fn))
        for nest in nested_archives:
            print(f"[INFO] 尝试解压嵌套包: {nest}")
            try:
                found = prepare_ota_package_for_upgrade(nest, t2_cfg, _depth + 1)
                if found:
                    break
            except Exception as e:
                print(f"[WARN] 嵌套解压失败: {e}")

    if not found:
        raise FileNotFoundError(f"解压后未找到可用 OTA 包: {extract_root}")

    # 若命中的仍是嵌套 .7z/.rar，再解一层
    fl = found.lower()
    if (fl.endswith(".7z") or fl.endswith(".rar")) and os.path.abspath(found) != path:
        print(f"[INFO] 检测到嵌套压缩包，继续解压: {found}")
        found = prepare_ota_package_for_upgrade(found, t2_cfg, _depth + 1)

    print(f"[INFO] 已解析出 SOC 升级包: {found}")
    return found


def fetch_package_to_local(source: str, save_dir: str, default_name: str = "update.zip") -> str:
    """
    将邮件中的升级包地址落到本地保存目录。
    支持：本地路径、UNC（\\\\IP\\share\\...）、http(s)://...
    返回本地绝对路径。
    """
    source = (source or "").strip().strip('"').strip("'")
    if not source:
        raise ValueError("升级包地址为空")

    os.makedirs(save_dir, exist_ok=True)
    file_name = _guess_file_name_from_source(source, default_name)
    dest = os.path.join(save_dir, file_name)

    # http(s)
    if re.match(r"^https?://", source, re.I):
        print(f"[INFO] 从 URL 下载升级包: {source}")
        return download_package(source, save_dir, file_name)

    # 规范化 UNC：把 //host/share 转成 \\host\share
    path = source
    if path.startswith("//") and not path.startswith("\\\\"):
        path = "\\\\" + path[2:].replace("/", "\\")

    if not os.path.isfile(path):
        raise FileNotFoundError(f"升级包源文件不存在或不可访问: {path}")

    # 已在目标目录则直接用
    try:
        if os.path.samefile(path, dest):
            print(f"[INFO] 升级包已在保存目录: {dest}")
            return os.path.abspath(dest)
    except Exception:
        pass

    if os.path.exists(dest):
        os.remove(dest)
    print(f"[INFO] 复制升级包: {path} -> {dest}")
    shutil.copy2(path, dest)
    print(f"[INFO] 复制完成: {dest} ({os.path.getsize(dest)/1024/1024:.2f} MB)")
    return os.path.abspath(dest)


def handle_email_trigger(t2_cfg: dict, bmc_cfg: dict, trigger: dict) -> int:
    """
    邮箱触发：解析到升级包地址后，先保存到本地 package_save_dir，
    再整理为可用 OTA 包，最后执行完整 SOC 升级（SocOtaUpgrade）。
    若仅有 OSS 字段（无 package 地址），则走原 OSS 下载路径。
    """
    source = (trigger.get("package_url") or trigger.get("package_path") or "").strip()
    if source:
        save_dir = _resolve_package_save_dir(t2_cfg)
        default_name = t2_cfg.get("upgrade_file_name", "update.zip") or "update.zip"
        print(f"[INFO] 邮箱触发：源地址={source}")
        print(f"[INFO] 升级包保存目录={save_dir}")
        try:
            local_pkg = fetch_package_to_local(source, save_dir, default_name)
            print(f"[INFO] 升级包已就绪(原始文件): {local_pkg}")
            upgrade_pkg = prepare_ota_package_for_upgrade(local_pkg, t2_cfg)
            print(f"[INFO] 复制/准备完成，开始完整 SOC 升级: {upgrade_pkg}")
        except Exception as e:
            print(f"[ERROR] 获取/准备升级包失败: {e}")
            return 3
        return execute_task(
            t2_cfg, bmc_cfg, upgrade_pkg,
            soc_version=trigger.get("soc_version", ""),
            parent_dir=trigger.get("parent_dir_name", ""),
            triggered_by_listen=True,
        )

    if trigger.get("soc_version") and trigger.get("parent_dir_name"):
        print(f"[INFO] 邮箱触发(OSS): soc_version={trigger['soc_version']} parent_dir={trigger['parent_dir_name']}")
        email_cfg = t2_cfg.get("email") or {}
        if email_cfg.get("package_save_dir"):
            t2_cfg = dict(t2_cfg)
            t2_cfg["download_dir"] = email_cfg["package_save_dir"]
        return handle_trigger(t2_cfg, bmc_cfg, trigger["soc_version"], trigger["parent_dir_name"])

    print(f"[WARN] 邮箱触发参数无效: {trigger}")
    return 2


def _load_processed_uids(path: str) -> set:
    if not path or not os.path.isfile(path):
        return set()
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return set(str(x) for x in data.get("uids", []))
    except Exception:
        return set()


def _save_processed_uids(path: str, uids: set):
    if not path:
        return
    abs_path = path if os.path.isabs(path) else os.path.normpath(os.path.join(SCRIPT_DIR, path))
    os.makedirs(os.path.dirname(abs_path) or ".", exist_ok=True)
    # 只保留最近 500 个，避免文件无限增长
    keep = sorted(uids)[-500:]
    with open(abs_path, "w", encoding="utf-8") as f:
        json.dump({"uids": keep, "updated": datetime.datetime.now().isoformat()}, f, ensure_ascii=False, indent=2)


def _email_login_usernames(username: str) -> list:
    """Exchange POP3/IMAP 常需短账号（yuhaohs），也兼容完整邮箱地址。"""
    u = (username or "").strip()
    if not u:
        return []
    names = [u]
    if "@" in u:
        local = u.split("@", 1)[0].strip()
        if local and local not in names:
            names.append(local)
    return names


def _resolve_mail_host(email_cfg: dict) -> str:
    return (email_cfg.get("mail_host")
            or email_cfg.get("pop3_host")
            or email_cfg.get("imap_host")
            or "").strip()


def _resolve_email_protocol(email_cfg: dict) -> str:
    proto = (email_cfg.get("protocol") or "auto").strip().lower()
    if proto in ("ews", "pop3", "imap", "auto"):
        return proto
    return "auto"


def _resolve_ews_url(email_cfg: dict) -> str:
    url = (email_cfg.get("ews_url") or "").strip()
    if url:
        return url
    host = _resolve_mail_host(email_cfg) or "webmail.hangsheng.com.cn"
    return f"https://{host}/EWS/Exchange.asmx"


def _imap_connect(email_cfg: dict):
    host = _resolve_mail_host(email_cfg)
    port = int(email_cfg.get("imap_port", 993))
    use_ssl = bool(email_cfg.get("use_ssl", True))
    if not host:
        raise RuntimeError("email.imap_host / mail_host 未配置")
    last_err = None
    mail = None
    for user in _email_login_usernames(email_cfg.get("username", "")):
        pwd = email_cfg.get("password", "")
        if not user or not pwd:
            raise RuntimeError("email.username / password 未配置")
        try:
            if use_ssl:
                mail = imaplib.IMAP4_SSL(host, port)
            else:
                mail = imaplib.IMAP4(host, port)
            mail.login(user, pwd)
            mailbox = email_cfg.get("mailbox", "INBOX")
            mail.select(mailbox)
            return mail
        except Exception as e:
            last_err = e
            try:
                if mail is not None:
                    mail.logout()
            except Exception:
                pass
            mail = None
    raise RuntimeError(f"IMAP 登录失败: {last_err}")


def _pop3_connect(email_cfg: dict):
    host = _resolve_mail_host(email_cfg)
    port = int(email_cfg.get("pop3_port", 995) or 995)
    if not host:
        raise RuntimeError("email 邮件主机未配置（mail_host / imap_host）")
    pwd = email_cfg.get("password", "")
    if not pwd:
        raise RuntimeError("email.password 未配置")
    last_err = None
    for user in _email_login_usernames(email_cfg.get("username", "")):
        pop = None
        try:
            pop = poplib.POP3_SSL(host, port, timeout=30)
            pop.user(user)
            pop.pass_(pwd)
            return pop
        except Exception as e:
            last_err = e
            try:
                if pop is not None:
                    pop.quit()
            except Exception:
                pass
    raise RuntimeError(f"POP3 登录失败: {last_err}")


def _poll_imap_once(email_cfg: dict, processed: set) -> list:
    results = []
    mail = None
    try:
        mail = _imap_connect(email_cfg)
        typ, data = mail.uid("search", None, "UNSEEN")
        if typ != "OK" or not data or not data[0]:
            return results
        uids = data[0].split()
        subject_kw = (email_cfg.get("subject_keyword") or "").strip()
        from_kw = (email_cfg.get("from_keyword") or "").strip()

        for uid in uids:
            uid_s = uid.decode() if isinstance(uid, bytes) else str(uid)
            if uid_s in processed:
                continue
            typ, msg_data = mail.uid("fetch", uid, "(RFC822)")
            if typ != "OK" or not msg_data or not msg_data[0]:
                continue
            raw = msg_data[0][1]
            msg = email.message_from_bytes(raw)
            subject = _decode_mime_header(msg.get("Subject", ""))
            from_addr = _decode_mime_header(msg.get("From", ""))
            text = _extract_email_text(msg)
            if not _mail_match_keyword(subject, text, subject_kw):
                processed.add(uid_s)
                if email_cfg.get("mark_seen", True):
                    mail.uid("store", uid, "+FLAGS", "(\\Seen)")
                continue
            if from_kw and from_kw.lower() not in from_addr.lower():
                processed.add(uid_s)
                if email_cfg.get("mark_seen", True):
                    mail.uid("store", uid, "+FLAGS", "(\\Seen)")
                continue

            trigger = _parse_trigger_from_mail(subject, text)
            if not trigger:
                # 无升级参数：记已处理，避免反复扫
                processed.add(uid_s)
                if email_cfg.get("mark_seen", True):
                    mail.uid("store", uid, "+FLAGS", "(\\Seen)")
                print(f"[WARN] 邮件 UID={uid_s} 主题={subject!r} 未解析到升级参数，已跳过")
                continue
            # 有 trigger：暂不 mark processed，由 listen 循环在升级终态后记账（支持失败重试）
            if email_cfg.get("mark_seen", True):
                mail.uid("store", uid, "+FLAGS", "(\\Seen)")
            results.append({"uid": uid_s, "subject": subject, "from": from_addr, "trigger": trigger})
    finally:
        if mail is not None:
            try:
                mail.logout()
            except Exception:
                pass
    return results


def _poll_pop3_once(email_cfg: dict, processed: set) -> list:
    """
    通过 POP3（与 OWA/Outlook 同机，Exchange 常开 995）拉取最近邮件。
    用 UIDL 去重；不删除服务器邮件。
    """
    results = []
    pop = None
    try:
        pop = _pop3_connect(email_cfg)
        count, _ = pop.stat()
        if count <= 0:
            return results

        # uidl: [(b'1', b'xxxx'), ...] 或 list 返回 bytes 行
        uid_map = {}
        try:
            resp, listings, _ = pop.uidl()
            for line in listings:
                parts = line.split()
                if len(parts) >= 2:
                    num = int(parts[0])
                    uid = parts[1].decode() if isinstance(parts[1], bytes) else str(parts[1])
                    uid_map[num] = "pop3:" + uid
        except Exception:
            uid_map = {}

        subject_kw = (email_cfg.get("subject_keyword") or "").strip()
        from_kw = (email_cfg.get("from_keyword") or "").strip()
        scan = int(email_cfg.get("pop3_scan_recent", 40) or 40)
        start = max(1, count - scan + 1)

        for num in range(count, start - 1, -1):
            uid_s = uid_map.get(num, f"pop3:msg-{num}")
            if uid_s in processed:
                continue
            try:
                _resp, lines, _octets = pop.retr(num)
                raw = b"\r\n".join(lines)
                msg = email.message_from_bytes(raw)
            except Exception as e:
                print(f"[WARN] POP3 读取邮件 #{num} 失败: {e}")
                processed.add(uid_s)
                continue

            subject = _decode_mime_header(msg.get("Subject", ""))
            from_addr = _decode_mime_header(msg.get("From", ""))
            text = _extract_email_text(msg)
            if not _mail_match_keyword(subject, text, subject_kw):
                processed.add(uid_s)
                continue
            if from_kw and from_kw.lower() not in from_addr.lower():
                processed.add(uid_s)
                continue

            trigger = _parse_trigger_from_mail(subject, text)
            if not trigger:
                processed.add(uid_s)
                print(f"[WARN] 邮件 UID={uid_s} 主题={subject!r} 未解析到升级参数，已跳过")
                continue
            # 有 trigger：升级终态后再 mark（见 email_listen_loop）
            results.append({"uid": uid_s, "subject": subject, "from": from_addr, "trigger": trigger})
    finally:
        if pop is not None:
            try:
                pop.quit()
            except Exception:
                pass
    # 保持与扫描顺序一致：新邮件优先
    return results


def _ews_soap_request(email_cfg: dict, soap: str) -> str:
    """
    通过 PowerShell + NTLM 调用 Exchange EWS（与 OWA/Outlook 同源）。
    返回响应 XML 文本。
    """
    ews_url = _resolve_ews_url(email_cfg)
    users = _email_login_usernames(email_cfg.get("username", ""))
    pwd = email_cfg.get("password", "")
    if not users or not pwd:
        raise RuntimeError("email.username / password 未配置")

    last_err = None
    for user in users:
        with tempfile.TemporaryDirectory(prefix="t2_ews_") as td:
            soap_path = os.path.join(td, "req.xml")
            out_path = os.path.join(td, "resp.xml")
            err_path = os.path.join(td, "err.txt")
            with open(soap_path, "w", encoding="utf-8") as f:
                f.write(soap)
            # 用环境变量传密码，避免命令行泄露到进程列表过久
            env = os.environ.copy()
            env["T2_EWS_USER"] = user
            env["T2_EWS_PASS"] = pwd
            env["T2_EWS_URL"] = ews_url
            ps = f"""
$ErrorActionPreference = 'Stop'
$user = $env:T2_EWS_USER
$pass = ConvertTo-SecureString $env:T2_EWS_PASS -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential($user, $pass)
$url = $env:T2_EWS_URL
$soap = Get-Content -Raw -Encoding UTF8 '{soap_path}'
try {{
  $resp = Invoke-WebRequest -Uri $url -Method Post -Body $soap -ContentType 'text/xml; charset=utf-8' -Credential $cred -TimeoutSec 60 -UseBasicParsing
  [System.IO.File]::WriteAllText('{out_path}', $resp.Content, [System.Text.UTF8Encoding]::new($false))
}} catch {{
  $msg = $_.Exception.Message
  if ($_.Exception.Response) {{
    try {{
      $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
      $msg = $msg + "`n" + $sr.ReadToEnd()
    }} catch {{}}
  }}
  [System.IO.File]::WriteAllText('{err_path}', $msg, [System.Text.UTF8Encoding]::new($false))
  exit 2
}}
"""
            try:
                cp = subprocess.run(
                    ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps],
                    env=env,
                    capture_output=True,
                    text=True,
                    timeout=90,
                )
            except Exception as e:
                last_err = e
                continue
            if cp.returncode == 0 and os.path.isfile(out_path):
                with open(out_path, "r", encoding="utf-8") as f:
                    return f.read()
            err = ""
            if os.path.isfile(err_path):
                with open(err_path, "r", encoding="utf-8") as f:
                    err = f.read().strip()
            last_err = err or (cp.stderr or cp.stdout or f"exit={cp.returncode}")
    raise RuntimeError(f"EWS 请求失败: {last_err}")


def _ews_local(tag: str) -> str:
    return "{http://schemas.microsoft.com/exchange/services/2006/types}" + tag


def _ews_find_recent_ids(email_cfg: dict, count: int = 20) -> list:
    """返回 [(item_id, change_key, subject, from_addr, received), ...] 新到旧。"""
    soap = f"""<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages"
 xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
 xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
  <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
  <soap:Body>
    <m:FindItem Traversal="Shallow">
      <m:ItemShape>
        <t:BaseShape>IdOnly</t:BaseShape>
        <t:AdditionalProperties>
          <t:FieldURI FieldURI="item:Subject"/>
          <t:FieldURI FieldURI="item:DateTimeReceived"/>
          <t:FieldURI FieldURI="message:From"/>
        </t:AdditionalProperties>
      </m:ItemShape>
      <m:IndexedPageItemView MaxEntriesReturned="{int(count)}" Offset="0" BasePoint="Beginning"/>
      <m:SortOrder>
        <t:FieldOrder Order="Descending"><t:FieldURI FieldURI="item:DateTimeReceived"/></t:FieldOrder>
      </m:SortOrder>
      <m:ParentFolderIds><t:DistinguishedFolderId Id="inbox"/></m:ParentFolderIds>
    </m:FindItem>
  </soap:Body>
</soap:Envelope>"""
    xml = _ews_soap_request(email_cfg, soap)
    root = ET.fromstring(xml)
    out = []
    for msg in root.iter(_ews_local("Message")):
        iid = msg.find(_ews_local("ItemId"))
        if iid is None:
            continue
        item_id = iid.attrib.get("Id", "")
        ck = iid.attrib.get("ChangeKey", "")
        subj_el = msg.find(_ews_local("Subject"))
        subject = html_lib.unescape(subj_el.text or "") if subj_el is not None else ""
        dt_el = msg.find(_ews_local("DateTimeReceived"))
        received = dt_el.text if dt_el is not None else ""
        from_addr = ""
        frm = msg.find(_ews_local("From"))
        if frm is not None:
            mb = frm.find(_ews_local("Mailbox"))
            if mb is not None:
                name_el = mb.find(_ews_local("Name"))
                mail_el = mb.find(_ews_local("EmailAddress"))
                name = name_el.text if name_el is not None else ""
                addr = mail_el.text if mail_el is not None else ""
                from_addr = f"{name} <{addr}>" if name or addr else ""
        out.append((item_id, ck, subject, from_addr, received))
    return out


def _ews_get_bodies(email_cfg: dict, items: list) -> dict:
    """items: [(id, change_key), ...] -> {id: body_text}"""
    if not items:
        return {}
    ids_xml = []
    for item_id, ck in items:
        if ck:
            ids_xml.append(f'<t:ItemId Id="{html_lib.escape(item_id, quote=True)}" ChangeKey="{html_lib.escape(ck, quote=True)}"/>')
        else:
            ids_xml.append(f'<t:ItemId Id="{html_lib.escape(item_id, quote=True)}"/>')
    soap = f"""<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages"
 xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
 xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
  <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
  <soap:Body>
    <m:GetItem>
      <m:ItemShape>
        <t:BaseShape>IdOnly</t:BaseShape>
        <t:BodyType>Text</t:BodyType>
        <t:AdditionalProperties>
          <t:FieldURI FieldURI="item:Body"/>
          <t:FieldURI FieldURI="item:Subject"/>
        </t:AdditionalProperties>
      </m:ItemShape>
      <m:ItemIds>
        {''.join(ids_xml)}
      </m:ItemIds>
    </m:GetItem>
  </soap:Body>
</soap:Envelope>"""
    xml = _ews_soap_request(email_cfg, soap)
    root = ET.fromstring(xml)
    bodies = {}
    for msg in root.iter(_ews_local("Message")):
        iid = msg.find(_ews_local("ItemId"))
        body_el = msg.find(_ews_local("Body"))
        if iid is None:
            continue
        body = html_lib.unescape(body_el.text or "") if body_el is not None else ""
        bodies[iid.attrib.get("Id", "")] = body
    return bodies


def _poll_ews_once(email_cfg: dict, processed: set) -> list:
    """通过 EWS 拉取最近邮件（与 OWA 收件箱一致）。"""
    results = []
    scan = int(email_cfg.get("ews_scan_recent", email_cfg.get("pop3_scan_recent", 20)) or 20)
    subject_kw = (email_cfg.get("subject_keyword") or "").strip()
    from_kw = (email_cfg.get("from_keyword") or "").strip()

    recent = _ews_find_recent_ids(email_cfg, scan)
    need_body = []
    meta = {}
    for item_id, ck, subject, from_addr, _received in recent:
        uid_s = "ews:" + item_id
        if uid_s in processed:
            continue
        meta[item_id] = (ck, subject, from_addr)
        need_body.append((item_id, ck))

    bodies = _ews_get_bodies(email_cfg, need_body) if need_body else {}
    for item_id, ck, subject, from_addr, _received in recent:
        uid_s = "ews:" + item_id
        if uid_s in processed:
            continue
        body = bodies.get(item_id, "")
        if not _mail_match_keyword(subject, body, subject_kw):
            processed.add(uid_s)
            continue
        if from_kw and from_kw.lower() not in (from_addr or "").lower():
            processed.add(uid_s)
            continue
        trigger = _parse_trigger_from_mail(subject, body)
        if not trigger:
            processed.add(uid_s)
            print(f"[WARN] 邮件 UID={uid_s} 主题={subject!r} 未解析到升级参数，已跳过")
            continue
        # 有 trigger：升级终态后再 mark（见 email_listen_loop）
        results.append({"uid": uid_s, "subject": subject, "from": from_addr, "trigger": trigger})
    return results


def poll_email_once(email_cfg: dict, processed: set) -> list:
    """
    拉取待处理升级邮件。
    protocol=ews：Exchange Web Services（与 OWA/Outlook 一致，推荐）
    protocol=pop3 / imap / auto
    """
    proto = _resolve_email_protocol(email_cfg)
    if proto == "ews":
        return _poll_ews_once(email_cfg, processed)
    if proto == "pop3":
        return _poll_pop3_once(email_cfg, processed)
    if proto == "imap":
        return _poll_imap_once(email_cfg, processed)

    # auto：优先 EWS，再 POP3，最后 IMAP
    errors = []
    for name, fn in (("EWS", _poll_ews_once), ("POP3", _poll_pop3_once), ("IMAP", _poll_imap_once)):
        try:
            return fn(email_cfg, processed)
        except Exception as e:
            errors.append(f"{name}: {e}")
    raise RuntimeError("邮箱轮询失败（" + " | ".join(errors) + "）")


def simulate_email_trigger(t2_cfg: dict, bmc_cfg: dict, source: str, fetch_only: bool = False) -> int:
    """
    模拟一封含升级包地址的邮件触发。
    source 可为本地路径 / UNC / http(s) URL。
    fetch_only=True 时只下载/复制到 package_save_dir，并打印 LOCAL_PACKAGE=<path>（供 UI 填入）。
    """
    source = (source or "").strip().strip('"').strip("'")
    if not source:
        print("[ERROR] --source 不能为空")
        return 2

    if fetch_only:
        save_dir = _resolve_package_save_dir(t2_cfg)
        default_name = t2_cfg.get("upgrade_file_name", "update.zip") or "update.zip"
        print(f"[INFO] 模拟邮件(仅拉取): 源={source}")
        print(f"[INFO] 保存目录={save_dir}")
        try:
            local_pkg = fetch_package_to_local(source, save_dir, default_name)
        except Exception as e:
            print(f"[ERROR] 获取/保存升级包失败: {e}")
            return 3
        print(f"LOCAL_PACKAGE={local_pkg}")
        return 0

    trigger = {"package_url": source, "package_path": source}
    print(f"[INFO] 模拟邮件触发升级: {source}")
    return handle_email_trigger(t2_cfg, bmc_cfg, trigger)


def email_listen_loop(t2_cfg: dict, bmc_cfg: dict, pause_event: threading.Event, stop_event: threading.Event):
    """后台线程：按 poll_interval 轮询邮箱（EWS/POP3/IMAP）并触发升级。"""
    email_cfg = t2_cfg.get("email") or {}
    if not email_cfg.get("enabled", False):
        return
    interval = int(email_cfg.get("poll_interval_sec", 60) or 60)
    uid_file = email_cfg.get("processed_uid_file", "./t2_email_processed_uids.json")
    abs_uid = uid_file if os.path.isabs(uid_file) else os.path.normpath(os.path.join(SCRIPT_DIR, uid_file))
    processed = _load_processed_uids(abs_uid)
    host = _resolve_mail_host(email_cfg)
    proto = _resolve_email_protocol(email_cfg)
    if proto == "ews":
        endpoint = _resolve_ews_url(email_cfg)
        print(f"[INFO] 邮箱监听已启动（EWS {endpoint}，"
              f"每 {interval}s 轮询，关键字={email_cfg.get('subject_keyword')!r}；匹配主题或正文）")
    else:
        if proto == "pop3":
            port = int(email_cfg.get("pop3_port", 995) or 995)
        elif proto == "imap":
            port = int(email_cfg.get("imap_port", 993) or 993)
        else:
            port = 995
        print(f"[INFO] 邮箱监听已启动（{proto.upper()} {host}:{port}，"
              f"每 {interval}s 轮询，关键字={email_cfg.get('subject_keyword')!r}；匹配主题或正文）")

    while not stop_event.is_set():
        if pause_event.is_set():
            stop_event.wait(2)
            continue
        try:
            items = poll_email_once(email_cfg, processed)
            _save_processed_uids(abs_uid, processed)
            policy = (email_cfg.get("mark_processed_policy") or "after_attempt").strip().lower()
            for item in items:
                print(f"[INFO] 收到升级邮件 UID={item['uid']} From={item.get('from','')} Subject={item['subject']!r}")
                pause_event.set()
                exit_code = 1
                try:
                    print("[INFO] ===== 流水线：下载/准备 → 完整 SOC 升级 → 检查/上报 =====")
                    exit_code = handle_email_trigger(t2_cfg, bmc_cfg, item["trigger"])
                    print(f"[INFO] 邮件触发流水线结束 UID={item['uid']} exit={exit_code}")
                except Exception as e:
                    print(f"[ERROR] 邮箱触发升级异常: {e}")
                    exit_code = 1
                finally:
                    pause_event.clear()

                # UID 记账策略：
                #   after_attempt（默认）——本次尝试结束后标记，避免杀进程中途丢失导致永不重试，同时避免永久失败死循环
                #   after_success ——仅成功才标记，失败下一轮自动重试
                should_mark = True
                if policy == "after_success" and exit_code != 0:
                    should_mark = False
                    print(f"[INFO] mark_processed_policy=after_success，升级未成功，保留 UID 以便重试: {item['uid']}")
                if should_mark:
                    processed.add(item["uid"])
                    _save_processed_uids(abs_uid, processed)
                    print(f"[INFO] 已标记邮件已处理: {item['uid']}")
        except Exception as e:
            print(f"[WARN] 邮箱轮询异常: {e}")
        stop_event.wait(interval)


# ---------------------------------------------------------------------------
# TCP 监听（兼容 build_pigeon 协议）
# ---------------------------------------------------------------------------
def tcp_listen(t2_cfg: dict, bmc_cfg: dict, host: str, port: int):
    tcp = t2_cfg.get("tcp", {})
    host = host or tcp.get("host", "0.0.0.0")
    port = port or tcp.get("port", 8007)

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((host, port))
    srv.listen(5)
    srv.settimeout(30)
    print(f"[INFO] T2 升级入口监听中 {host}:{port}（等待 build_pigeon 触发，Ctrl+C 退出）")

    # 启动空闲心跳线程
    pause_event = threading.Event()
    stop_event = threading.Event()
    hb_thread = threading.Thread(target=idle_heartbeat_loop, args=(bmc_cfg, t2_cfg, pause_event, stop_event), daemon=True)
    hb_thread.start()

    # 邮箱监听（配置 enabled=true 时与 TCP 并行）
    email_cfg = t2_cfg.get("email") or {}
    if email_cfg.get("enabled", False):
        email_thread = threading.Thread(
            target=email_listen_loop, args=(t2_cfg, bmc_cfg, pause_event, stop_event), daemon=True)
        email_thread.start()
    else:
        print("[INFO] 邮箱监听未启用（email.enabled=false），仅 TCP 触发")

    try:
        while True:
            try:
                conn, addr = srv.accept()
            except socket.timeout:
                continue
            print(f"[INFO] 收到连接: {addr}")
            try:
                conn.settimeout(5)
                data = conn.recv(4096).decode("utf-8")
                if not data:
                    continue
                msg = json.loads(data)
                # 兼容两种触发消息：
                #   1) 本地模拟/直传：{"package_path": "<本地升级包路径>"}  → 跳过 OSS，直接升级
                #   2) build_pigeon 真链路：{"soc_version": "...", "parent_dir_name": "..."} → OSS 下载后升级
                if "package_path" in msg and msg["package_path"]:
                    conn.send(b"ACK")
                    print(f"[INFO] 触发消息(本地包,监听): {msg}")
                    pkg = os.path.abspath(msg["package_path"])
                    pause_event.set()
                    try:
                        execute_task(t2_cfg, bmc_cfg, pkg,
                                     soc_version=msg.get("soc_version", ""),
                                     parent_dir=msg.get("parent_dir_name", ""),
                                     triggered_by_listen=True)
                    finally:
                        pause_event.clear()
                elif all(k in msg for k in ("soc_version", "parent_dir_name")):
                    conn.send(b"ACK")
                    print(f"[INFO] 触发消息(OSS): {msg}")
                    pause_event.set()
                    try:
                        handle_trigger(t2_cfg, bmc_cfg, msg["soc_version"], msg["parent_dir_name"])
                    finally:
                        pause_event.clear()
                else:
                    print(f"[WARN] 消息缺字段(需 package_path 或 soc_version+parent_dir_name): {msg}")
                    conn.send(b"NACK")
            except Exception as e:
                print(f"[ERROR] 处理连接异常: {e}")
            finally:
                conn.close()
    except KeyboardInterrupt:
        print("\n[INFO] 退出监听")
    finally:
        stop_event.set()
        srv.close()


def email_listen_only(t2_cfg: dict, bmc_cfg: dict):
    """仅邮箱监听模式（不启 TCP）。"""
    email_cfg = t2_cfg.get("email") or {}
    if not email_cfg.get("enabled", False):
        # 强制启用本次运行
        email_cfg = dict(email_cfg)
        email_cfg["enabled"] = True
        t2_cfg = dict(t2_cfg)
        t2_cfg["email"] = email_cfg
        print("[WARN] 配置 email.enabled=false，--listen-email 强制启用本次邮箱监听")

    pause_event = threading.Event()
    stop_event = threading.Event()
    hb_thread = threading.Thread(target=idle_heartbeat_loop, args=(bmc_cfg, t2_cfg, pause_event, stop_event), daemon=True)
    hb_thread.start()
    try:
        email_listen_loop(t2_cfg, bmc_cfg, pause_event, stop_event)
    except KeyboardInterrupt:
        print("\n[INFO] 退出邮箱监听")
    finally:
        stop_event.set()


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
def main():
    parser = argparse.ArgumentParser(
        description="T2 ATF 升级入口（脚本驱动）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  # 直传升级包
  python t2_upgrade_entry.py --package "D:\\ota\\update.zip"

  # 监听 build_pigeon 触发（默认；若 email.enabled=true 则同时监听邮箱）
  python t2_upgrade_entry.py --listen

  # 仅监听邮箱
  python t2_upgrade_entry.py --listen-email

  # 模拟邮件触发（完整升级）
  python t2_upgrade_entry.py --simulate-email --source "E:\\BMCSJB\\update.zip"

  # 模拟邮件仅拉取到保存目录（供 UI 填入包路径）
  python t2_upgrade_entry.py --simulate-email --source "E:\\BMCSJB\\update.zip" --fetch-only

  # 指定端口监听
  python t2_upgrade_entry.py --listen --port 8007
        """,
    )
    parser.add_argument("--package", help="直接指定本地升级包路径，跳过 TCP/OSS")
    parser.add_argument("--listen", action="store_true", help="监听 TCP 接收 build_pigeon 触发")
    parser.add_argument("--listen-email", action="store_true", help="仅监听邮箱（IMAP）触发升级")
    parser.add_argument("--simulate-email", action="store_true",
                        help="模拟邮件触发：按 --source 拉取/复制升级包并执行升级")
    parser.add_argument("--source", default="",
                        help="模拟邮件的升级包地址（本地路径/UNC/http）")
    parser.add_argument("--fetch-only", action="store_true",
                        help="与 --simulate-email 联用：只拉取到 package_save_dir，不启动升级")
    parser.add_argument("--host", default="", help="TCP 监听地址（默认取配置）")
    parser.add_argument("--port", type=int, default=0, help="TCP 监听端口（默认取配置）")
    parser.add_argument("--config", default=os.path.join(SCRIPT_DIR, "t2_atf_config.json"),
                        help="T2 入口配置文件路径")
    args = parser.parse_args()

    if not os.path.isfile(args.config):
        print(f"[ERROR] 配置不存在: {args.config}")
        return 1

    t2_cfg = load_t2_config(args.config)
    bmc_cfg = load_bmc_config(t2_cfg)

    if args.simulate_email:
        return simulate_email_trigger(t2_cfg, bmc_cfg, args.source, fetch_only=args.fetch_only)

    if args.package:
        if not os.path.isfile(args.package):
            print(f"[ERROR] 升级包不存在: {args.package}")
            return 2
        return execute_task(t2_cfg, bmc_cfg, os.path.abspath(args.package))

    if args.listen_email:
        email_listen_only(t2_cfg, bmc_cfg)
        return 0

    # 无 --package 即监听模式（--listen 可省略）；email.enabled=true 时并行邮箱轮询
    tcp_listen(t2_cfg, bmc_cfg, args.host, args.port)
    return 0


if __name__ == "__main__":
    sys.exit(main())
