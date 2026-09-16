# -*- coding: utf-8 -*-
"""
T2 脚本池执行器（阶段 08，HAL 对齐）

支持两类条目：
1) kind=hal_module：执行 scripts/hal_selfcheck/<module_id>/run.sh，拉取 module_result.json
2) kind=shell（默认）：兼容旧版泛化 .sh

执行结束后可汇总生成 detect/detect_result.html（见 t2_detect_report.py）。
"""

from __future__ import annotations

import json
import os
import subprocess
import time
from typing import Any


def _adb_exe(scripts_dir: str) -> str:
    candidates = [
        os.path.join(scripts_dir, "SocOtaUpgrade", "tools", "adb.exe"),
        os.path.join(scripts_dir, "soc_ota_tool", "bundled", "adb.exe"),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c
    return "adb"


def _path_has_non_ascii(path: str) -> bool:
    try:
        path.encode("ascii")
        return False
    except UnicodeEncodeError:
        return True


def _stage_module_for_adb_push(module_dir: str, module_id: str) -> str:
    """
    本机路径含中文时，adb push 会把远端目录名截断（如 vehicleconfig→vehicle），
    导致找不到 run.sh。先复制到 %TEMP%/t2_hal_push/<module_id>/ 再 push。
    同时将 .sh 转为 LF，避免 Windows CRLF 在车机 ash/sh 下语法错误。
    """
    import shutil
    import tempfile

    need_stage = _path_has_non_ascii(module_dir)
    # 即使路径纯 ASCII，Windows 检出的 .sh 也常带 CRLF，统一 staging 转换更稳
    stage_root = os.path.join(tempfile.gettempdir(), "t2_hal_push", module_id)
    if os.path.isdir(stage_root):
        shutil.rmtree(stage_root, ignore_errors=True)
    os.makedirs(os.path.dirname(stage_root), exist_ok=True)
    shutil.copytree(module_dir, stage_root)
    for root, _dirs, files in os.walk(stage_root):
        for name in files:
            if not name.lower().endswith((".sh", ".txt")):
                continue
            fp = os.path.join(root, name)
            try:
                raw = open(fp, "rb").read()
                if b"\r\n" in raw or raw.endswith(b"\r"):
                    open(fp, "wb").write(raw.replace(b"\r\n", b"\n").replace(b"\r", b"\n"))
            except Exception:
                pass
    if need_stage:
        print(f"[INFO] adb push 使用 ASCII 临时目录（避免中文路径截断）: {stage_root}")
    else:
        print(f"[INFO] adb push 使用临时目录（统一 LF 换行）: {stage_root}")
    return stage_root


def _resolve_dep_local(local: str, module_dir: str, scripts_dir: str) -> str:
    """解析 device_deps.local：绝对路径 / 相对模块目录 / 相对 scripts_dir。"""
    if not local:
        return ""
    if os.path.isabs(local) and os.path.isfile(local):
        return local
    cand = [
        os.path.normpath(os.path.join(module_dir, local)),
        os.path.normpath(os.path.join(scripts_dir, local)),
        local,
    ]
    for p in cand:
        if os.path.isfile(p):
            return p
    return ""


def _push_device_deps(
    adb_base: list[str],
    deps: list,
    module_dir: str,
    scripts_dir: str,
) -> list[str]:
    """
    将模块声明的依赖文件 push 到车机指定路径。
    deps 项示例：
      {"local": "E:/BMCSJB/test864.txt", "remote": "/vendor/1190.txt", "require_remount": true}
      {"local": "bin/libhwbaseio.so", "remote": "/vendor/lib64/libhwbaseio.so"}
    返回已 push 的描述列表；缺文件或 push 失败抛异常。
    """
    if not deps:
        return []
    done: list[str] = []
    need_remount = any(bool(d.get("require_remount")) for d in deps if isinstance(d, dict))
    if need_remount:
        subprocess.run(adb_base + ["root"], capture_output=True, timeout=30)
        time.sleep(0.5)
        subprocess.run(adb_base + ["wait-for-device"], capture_output=True, timeout=60)
        rem = subprocess.run(adb_base + ["remount"], capture_output=True, text=True, timeout=60)
        print(f"[INFO] device_deps remount: {(rem.stdout or rem.stderr or '').strip()[:200]}")

    for d in deps:
        if not isinstance(d, dict):
            continue
        local = str(d.get("local") or "").strip()
        remote = str(d.get("remote") or "").strip()
        desc = str(d.get("description") or "").strip()
        if not local or not remote:
            continue
        src = _resolve_dep_local(local, module_dir, scripts_dir)
        if not src:
            raise RuntimeError(f"device_deps 本地文件不存在: local={local}")
        # 确保远端父目录存在
        parent = remote.rsplit("/", 1)[0]
        if parent:
            subprocess.run(adb_base + ["shell", f"mkdir -p {parent}"], capture_output=True, timeout=20)
        push = subprocess.run(
            adb_base + ["push", src, remote],
            capture_output=True, text=True, timeout=120,
        )
        if push.returncode != 0:
            raise RuntimeError(
                f"device_deps push 失败: {src} -> {remote}\n{push.stderr or push.stdout}"
            )
        # vendor 分区常见需 chmod
        subprocess.run(adb_base + ["shell", f"chmod 644 {remote} 2>/dev/null || true"], capture_output=True, timeout=20)
        label = desc or f"{os.path.basename(src)} -> {remote}"
        print(f"[INFO] device_deps push OK: {label}")
        done.append(label)
    return done


def _resolve_remote_run_sh(adb_base: list[str], remote_dir: str, module_id: str) -> tuple[str, str]:
    """
    返回 (remote_dir_for_out, remote_run_sh)。
    兼容：扁平 / NEST(module_id) / 中文截断(vehicle) / find 兜底。
    """
    candidates = [
        f"{remote_dir}/run.sh",
        f"{remote_dir}/{module_id}/run.sh",
    ]
    # 常见截断：vehicleconfig → vehicle
    if len(module_id) > 6:
        candidates.append(f"{remote_dir}/{module_id[:7]}/run.sh")
        candidates.append(f"{remote_dir}/{module_id[:6]}/run.sh")

    for path in candidates:
        chk = subprocess.run(
            adb_base + ["shell", f"test -f {path} && echo OK"],
            capture_output=True, text=True, timeout=20,
        )
        if "OK" in (chk.stdout or ""):
            parent = path.rsplit("/", 1)[0]
            return parent, path

    find = subprocess.run(
        adb_base + ["shell", f"find {remote_dir} -type f -name run.sh 2>/dev/null | head -1"],
        capture_output=True, text=True, timeout=30,
    )
    found = (find.stdout or "").strip().splitlines()
    if found and found[0].strip():
        path = found[0].strip()
        parent = path.rsplit("/", 1)[0]
        print(f"[INFO] 通过 find 定位 run.sh: {path}")
        return parent, path

    listing = subprocess.run(
        adb_base + ["shell", f"ls -laR {remote_dir} 2>&1 | head -40"],
        capture_output=True, text=True, timeout=20,
    )
    detail = (listing.stdout or listing.stderr or "").strip()
    raise RuntimeError(
        f"车机未找到 run.sh（push 后）: {remote_dir}\n远端目录内容:\n{detail}"
    )



def load_script_pool(pool_path: str) -> dict:
    if not pool_path or not os.path.isfile(pool_path):
        return {"enabled": False, "scripts": []}
    with open(pool_path, "r", encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        return {"enabled": False, "scripts": []}
    data.setdefault("scripts", [])
    return data


def _load_manifest(module_dir: str) -> dict:
    mp = os.path.join(module_dir, "manifest.json")
    if not os.path.isfile(mp):
        return {}
    with open(mp, "r", encoding="utf-8") as f:
        return json.load(f)


def _run_hal_module(
    scripts_dir: str,
    module_id: str,
    session_dir: str,
    adb: str,
    adb_serial: str,
    timeout: int,
    remote_root: str,
    script_path: str = "",
    device_deps: list | None = None,
) -> dict:
    """在车机执行 selfcheck/run.sh，拉取 module_result.json。"""
    if script_path:
        sp = script_path if os.path.isabs(script_path) else os.path.normpath(os.path.join(scripts_dir, script_path))
        if os.path.isfile(sp) and sp.lower().endswith(".sh"):
            module_dir = os.path.dirname(sp)
            run_sh = sp
        elif os.path.isdir(sp):
            module_dir = sp
            run_sh = os.path.join(module_dir, "run.sh")
        else:
            module_dir = os.path.join(scripts_dir, "hal_selfcheck", module_id)
            run_sh = os.path.join(module_dir, "run.sh")
    else:
        module_dir = os.path.join(scripts_dir, "hal_selfcheck", module_id)
        run_sh = os.path.join(module_dir, "run.sh")
    manifest = _load_manifest(module_dir)
    item = {
        "id": module_id,
        "name": manifest.get("module_name") or module_id,
        "kind": "hal_module",
        "exit_code": None,
        "duration_sec": 0,
        "passed": False,
        "skipped": False,
        "log": "",
        "error": "",
        "path": run_sh,
        "module_result": None,
        "device_deps_pushed": [],
    }
    if not os.path.isfile(run_sh):
        item["error"] = f"缺少 run.sh: {run_sh}"
        item["exit_code"] = 127
        return item

    remote_dir = f"{remote_root.rstrip('/')}/{module_id}"
    adb_base = [adb]
    if adb_serial:
        adb_base += ["-s", adb_serial]

    t0 = time.time()
    try:
        # 先 push 模块依赖文件（如测试车参 /vendor/1190.txt、缺失的 .so）
        deps = list(device_deps or [])
        if not deps and isinstance(manifest.get("device_deps"), list):
            deps = list(manifest.get("device_deps") or [])
        if deps:
            item["device_deps_pushed"] = _push_device_deps(adb_base, deps, module_dir, scripts_dir)

        push_src = _stage_module_for_adb_push(module_dir, module_id)
        # 推送整个模块目录（含 lib/cases/default_config 等）
        subprocess.run(adb_base + ["shell", f"rm -rf {remote_dir}"], capture_output=True, timeout=30)
        subprocess.run(adb_base + ["shell", f"mkdir -p {remote_dir}"], capture_output=True, timeout=30)
        # 推「目录内容」到 remote_dir/，避免再套一层同名目录
        push = subprocess.run(
            adb_base + ["push", push_src + "/.", remote_dir + "/"],
            capture_output=True, text=True, timeout=120,
        )
        if push.returncode != 0:
            # 部分 adb 不支持 /. 语法，回退整目录 push
            push = subprocess.run(
                adb_base + ["push", push_src, remote_dir],
                capture_output=True, text=True, timeout=120,
            )
        if push.returncode != 0:
            raise RuntimeError(f"adb push 模块目录失败: {push.stderr or push.stdout}")

        remote_dir, remote_script = _resolve_remote_run_sh(adb_base, remote_dir, module_id)

        subprocess.run(adb_base + ["shell", f"chmod -R 755 {remote_dir}"], capture_output=True, timeout=30)
        cp = subprocess.run(
            adb_base + ["shell", f"sh {remote_script} {remote_dir}"],
            capture_output=True, timeout=timeout,
        )
        out = ""
        try:
            out = ((cp.stdout or b"") + (cp.stderr or b"")).decode("utf-8", errors="replace")
        except Exception:
            out = str(cp.stdout or b"") + str(cp.stderr or b"")
        full_log = out or ""
        item["log"] = full_log[-20000:]
        item["exit_code"] = cp.returncode
        item["passed"] = cp.returncode == 0

        # 拉取 module_result.json + 模块测试日志
        local_mod_dir = os.path.join(session_dir, "detect", "modules", module_id)
        os.makedirs(local_mod_dir, exist_ok=True)
        remote_json = f"{remote_dir}/module_result.json"
        local_json = os.path.join(local_mod_dir, "module_result.json")
        pull = subprocess.run(
            adb_base + ["pull", remote_json, local_json],
            capture_output=True, text=True, timeout=30,
        )
        if os.path.isfile(local_json):
            with open(local_json, "r", encoding="utf-8") as f:
                item["module_result"] = json.load(f)
        elif pull.returncode != 0:
            # 无 JSON 时用 stdout 兜底构造
            item["module_result"] = {
                "schema_version": "1.0",
                "module_id": module_id,
                "module_name": item["name"],
                "overall": "pass" if item["passed"] else "fail",
                "summary": "module_result.json 未拉回，仅按退出码判定",
                "cases": [],
                "attachments": [],
            }
            with open(local_json, "w", encoding="utf-8") as f:
                json.dump(item["module_result"], f, ensure_ascii=False, indent=2)

        # 截取各模块测试日志（控制台 + 车机 logs/work_*）
        try:
            from t2_session_artifacts import pull_module_device_logs

            item["log_artifacts"] = pull_module_device_logs(
                adb_base, remote_dir, local_mod_dir, console_log=full_log
            )
        except Exception as e:
            item["log_artifacts"] = []
            print(f"[WARN] 拉取模块日志失败 {module_id}: {e}")
    except Exception as e:
        item["error"] = str(e)
        item["exit_code"] = -1
        item["passed"] = False
        item["module_result"] = {
            "schema_version": "1.0",
            "module_id": module_id,
            "module_name": item["name"],
            "overall": "error",
            "summary": str(e),
            "cases": [],
        }
    finally:
        item["duration_sec"] = round(time.time() - t0, 2)
    return item

def _run_shell_script(
    scripts_dir: str,
    sc: dict,
    session_dir: str,
    adb: str,
    adb_serial: str,
    timeout: int,
    remote_dir: str,
) -> dict:
    sid = str(sc.get("id") or sc.get("script_name") or sc.get("name") or "unknown")
    name = str(sc.get("name") or sc.get("script_desc") or sid)
    item = {
        "id": sid,
        "name": name,
        "kind": "shell",
        "exit_code": None,
        "duration_sec": 0,
        "passed": False,
        "skipped": False,
        "log": "",
        "error": "",
        "path": sc.get("path", ""),
        "module_result": None,
    }
    rel = sc.get("path") or ""
    script_path = rel if os.path.isabs(rel) else os.path.normpath(os.path.join(scripts_dir, rel))
    if not os.path.isfile(script_path):
        item["error"] = f"脚本不存在: {script_path}"
        item["exit_code"] = 127
        return item

    run_on = (sc.get("run_on") or "device").strip().lower()
    t0 = time.time()
    try:
        if run_on == "host":
            cp = subprocess.run(
                ["bash", script_path],
                capture_output=True, text=True, timeout=timeout,
                cwd=os.path.dirname(script_path),
            )
            out = (cp.stdout or "") + (cp.stderr or "")
            code = cp.returncode
        else:
            adb_base = [adb]
            if adb_serial:
                adb_base += ["-s", adb_serial]
            remote_path = f"{remote_dir.rstrip('/')}/{os.path.basename(script_path)}"
            subprocess.run(adb_base + ["shell", f"mkdir -p {remote_dir}"], capture_output=True, timeout=30)
            push = subprocess.run(
                adb_base + ["push", script_path, remote_path],
                capture_output=True, text=True, timeout=60,
            )
            if push.returncode != 0:
                raise RuntimeError(f"adb push 失败: {push.stderr or push.stdout}")
            subprocess.run(adb_base + ["shell", f"chmod 755 {remote_path}"], capture_output=True, timeout=30)
            cp = subprocess.run(
                adb_base + ["shell", f"sh {remote_path}"],
                capture_output=True, text=True, timeout=timeout,
            )
            out = (cp.stdout or "") + (cp.stderr or "")
            code = cp.returncode
        item["exit_code"] = code
        item["log"] = (out or "")[-8000:]
        item["passed"] = code == 0
    except Exception as e:
        item["error"] = str(e)
        item["exit_code"] = -1
        item["passed"] = False
    finally:
        item["duration_sec"] = round(time.time() - t0, 2)
    return item


def run_script_pool(
    scripts_dir: str,
    pool_cfg_path: str,
    session_dir: str = "",
    adb_serial: str = "",
    pc_id: str = "",
    pc_name: str = "",
    soc_version: str = "",
    build_detect_report: bool = True,
    parent_dir_name: str = "",
    package_path: str = "",
    version_verify: dict | None = None,
    hal_status: dict | None = None,
    progress: dict | None = None,
) -> dict:
    """
    执行脚本池。返回：
      Passed/PassCount/FailCount/SkipCount/Items/module_results/detect_dir/...
    """
    pool = load_script_pool(pool_cfg_path)
    result: dict[str, Any] = {
        "Passed": True,
        "PassCount": 0,
        "FailCount": 0,
        "SkipCount": 0,
        "Items": [],
        "module_results": [],
        "detect_dir": "",
        "pool_path": pool_cfg_path,
        "enabled": bool(pool.get("enabled", False)),
    }
    if not pool.get("enabled", False):
        print("[INFO] 脚本池未启用（script_pool.enabled=false），跳过")
        return result

    scripts = pool.get("scripts") or []
    if not scripts:
        print("[WARN] 脚本池列表为空")
        return result

    adb = _adb_exe(scripts_dir)
    fail_fast = bool(pool.get("fail_fast", False))
    remote_dir = pool.get("device_script_dir", "/data/local/tmp/t2_script_pool")
    remote_hal = pool.get("device_hal_selfcheck_dir", "/data/local/tmp/t2_selfcheck")
    log_root = session_dir or scripts_dir
    os.makedirs(log_root, exist_ok=True)
    item_log_dir = os.path.join(log_root, "script_pool_logs")
    os.makedirs(item_log_dir, exist_ok=True)

    print(f"[INFO] 开始执行脚本池：共 {len(scripts)} 项，adb={adb}")

    for sc in scripts:
        if not isinstance(sc, dict):
            continue
        enabled = bool(sc.get("enabled", True))
        sid = str(sc.get("id") or sc.get("module_id") or sc.get("script_name") or "unknown")
        if not enabled:
            item = {
                "id": sid,
                "name": sc.get("name") or sid,
                "kind": sc.get("kind") or "shell",
                "exit_code": None,
                "duration_sec": 0,
                "passed": True,
                "skipped": True,
                "log": "",
                "error": "",
                "path": sc.get("path", ""),
                "module_result": None,
            }
            result["SkipCount"] += 1
            result["Items"].append(item)
            print(f"[INFO] 脚本跳过(disabled): {sid}")
            continue

        kind = (sc.get("kind") or "").strip().lower()
        if not kind and sc.get("module_id"):
            kind = "hal_module"
        timeout = int(sc.get("timeout_sec") or pool.get("default_timeout_sec") or 120)

        if kind == "hal_module":
            mid = str(sc.get("module_id") or sc.get("id") or "")
            item = _run_hal_module(
                scripts_dir, mid, log_root, adb, adb_serial, timeout, remote_hal,
                script_path=str(sc.get("path") or ""),
                device_deps=sc.get("device_deps") if isinstance(sc.get("device_deps"), list) else None,
            )
        else:
            item = _run_shell_script(
                scripts_dir, sc, log_root, adb, adb_serial, timeout, remote_dir
            )

        # 写单项日志
        try:
            log_fp = os.path.join(item_log_dir, f"{item['id']}.log")
            with open(log_fp, "w", encoding="utf-8") as f:
                f.write(item.get("log") or "")
                if item.get("error"):
                    f.write("\n[error]\n" + item["error"])
            item["log_file"] = log_fp
            # 同步到模块目录，便于 HTML 报告引用
            mid = str(item.get("id") or "")
            mod_console = os.path.join(log_root, "detect", "modules", mid, "console.log")
            if mid and (item.get("log") or item.get("error")):
                os.makedirs(os.path.dirname(mod_console), exist_ok=True)
                if not os.path.isfile(mod_console):
                    with open(mod_console, "w", encoding="utf-8") as f:
                        f.write(item.get("log") or "")
                        if item.get("error"):
                            f.write("\n[error]\n" + item["error"])
        except Exception:
            pass

        if item.get("skipped"):
            result["SkipCount"] += 1
        elif item.get("passed"):
            result["PassCount"] += 1
        else:
            result["FailCount"] += 1
            result["Passed"] = False

        if item.get("module_result"):
            result["module_results"].append(item["module_result"])

        result["Items"].append(item)
        print(f"[INFO] {item.get('kind')} {item['id']}: exit={item.get('exit_code')} "
              f"({'Pass' if item.get('passed') else 'Fail'})")

        if fail_fast and not item.get("passed") and not item.get("skipped"):
            print("[INFO] fail_fast=true，停止后续脚本")
            break

    # 汇总 detect 报告
    if build_detect_report and (result["module_results"] or version_verify or hal_status or result["Items"]):
        try:
            from t2_detect_report import build_detect_dir
            detect_dir = build_detect_dir(
                log_root,
                result["module_results"],
                pc_id=pc_id,
                pc_name=pc_name,
                soc_version=soc_version,
                parent_dir_name=parent_dir_name,
                version_verify=version_verify,
                hal_status=hal_status,
                script_pool=result,
                package_path=package_path,
                progress=progress,
                scripts_dir=scripts_dir,
            )
            result["detect_dir"] = detect_dir
            print(f"[INFO] 已生成测试报告目录: {detect_dir}")
            print(f"[INFO] 总报告: {os.path.join(detect_dir, 'detect_result.html')}")
        except Exception as e:
            print(f"[WARN] 生成 detect_result.html 失败: {e}")

    out_path = os.path.join(log_root, "script_results.json")
    try:
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
        result["result_file"] = out_path
        print(f"[INFO] 脚本池结果: {out_path} "
              f"(Pass={result['PassCount']} Fail={result['FailCount']} Skip={result['SkipCount']})")
    except Exception as e:
        print(f"[WARN] 写 script_results.json 失败: {e}")

    return result


def collect_atf_script_entries(scripts_dir: str, pool_cfg_path: str = "") -> list[dict]:
    """
    从 hal_selfcheck/*/manifest.json（及可选 pool）收集可上架 ATF 的脚本条目。
    返回 [{script_name, script_desc, module_id, default_selected}, ...]
    """
    root = os.path.join(scripts_dir, "hal_selfcheck")
    entries = []
    if not os.path.isdir(root):
        return entries
    for name in sorted(os.listdir(root)):
        mdir = os.path.join(root, name)
        if not os.path.isdir(mdir):
            continue
        man = _load_manifest(mdir)
        if not man:
            continue
        plat = man.get("platform") or {}
        if plat.get("sync_to_atf") is False:
            continue
        entries.append({
            "script_name": man.get("script_name") or f"HalSelfCheck_{man.get('module_id', name)}.sh",
            "script_desc": man.get("script_desc") or man.get("module_name") or name,
            "module_id": man.get("module_id") or name,
            "default_selected": bool(plat.get("default_selected", True)),
            "tags": plat.get("tags") or [],
        })
    return entries


def _load_json_if_exists(path: str):
    if not path or not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def main(argv: list[str] | None = None) -> int:
    """CLI：供 SocOtaUpgrade GUI / 升级流水线调用（push→执行→pull→detect）。"""
    import argparse
    import sys

    ap = argparse.ArgumentParser(
        description="T2 脚本池执行器：按 t2_script_pool.json push 到车机、执行 run.sh、拉取结果并生成 detect 报告"
    )
    ap.add_argument("--scripts-dir", required=True, help="scripts 根目录（含 hal_selfcheck/）")
    ap.add_argument("--pool", default="", help="脚本池 JSON；默认 <scripts-dir>/t2_script_pool.json")
    ap.add_argument("--session-dir", required=True, help="会话日志目录（写入 script_results.json / detect/）")
    ap.add_argument("--adb-serial", default="", help="可选 adb -s 序列号")
    ap.add_argument("--pc-id", default="")
    ap.add_argument("--pc-name", default="")
    ap.add_argument("--soc-version", default="")
    ap.add_argument("--no-detect-report", action="store_true", help="不生成 detect_result.html")
    args = ap.parse_args(argv)

    scripts_dir = os.path.abspath(args.scripts_dir)
    pool = args.pool or os.path.join(scripts_dir, "t2_script_pool.json")
    session_dir = os.path.abspath(args.session_dir)
    os.makedirs(session_dir, exist_ok=True)

    result = run_script_pool(
        scripts_dir,
        pool,
        session_dir=session_dir,
        adb_serial=args.adb_serial or "",
        pc_id=args.pc_id or "",
        pc_name=args.pc_name or "",
        soc_version=args.soc_version or "",
        build_detect_report=not args.no_detect_report,
        version_verify=_load_json_if_exists(os.path.join(session_dir, "version_verify.json")),
        hal_status=_load_json_if_exists(os.path.join(session_dir, "hal_status.json")),
    )
    if not result.get("enabled", False):
        print("[INFO] 脚本池未启用，退出码 0")
        return 0
    return 0 if result.get("Passed", True) else 1


if __name__ == "__main__":
    raise SystemExit(main())
