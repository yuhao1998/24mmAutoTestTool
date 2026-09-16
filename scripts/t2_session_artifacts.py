# -*- coding: utf-8 -*-
"""
测试会话产物增强：
1) 截取各模块车机侧日志到 session
2) 生成测试思维导图（内容说明 + 结果）
3) 供 HTML 报告嵌入使用

落盘（每次测试 session 目录）：
  script_pool_logs/<module>.log          — 控制台输出
  detect/modules/<module>/console.log    — 同上副本
  detect/modules/<module>/device_logs/   — 车机 $OUT/logs 等
  test_mindmap.md / detect/test_mindmap.md
  test_summary.json / detect/test_summary.json
"""

from __future__ import annotations

import html as html_mod
import json
import os
import re
import subprocess
from datetime import datetime
from typing import Any


CASE_DESC: dict[str, str] = {
    "ipc": "服务二进制/可执行文件是否存在（可连接）",
    "binder": "HAL 服务进程是否存活（绑定正常）",
    "response": "接口有响应（lshal/槽位/工具探针）",
    "function_smoke": "功能冒烟（进程+接口/槽位可读）",
    "no_crash": "调用后无崩溃（进程仍在、无相关 tombstone）",
    "memleak_probe": "无内存泄漏探针（默认常跳过）",
    "write_read_restore": "合规写入+切面+全量读回+还原（四步法）",
    "write_invalid_expected_fail": "空文件/不合规写入预期失败",
    "read_invalid_configid": "不合规车参名读取预期失败",
    "persist_after_reboot": "重启后持久化校验",
    "concurrent_query": "多进程并发查询压测",
}


def pull_module_device_logs(
    adb_base: list[str],
    remote_dir: str,
    local_mod_dir: str,
    console_log: str = "",
) -> list[str]:
    """从车机拉取模块测试日志到 local_mod_dir。"""
    saved: list[str] = []
    os.makedirs(local_mod_dir, exist_ok=True)

    if console_log:
        console_path = os.path.join(local_mod_dir, "console.log")
        try:
            with open(console_path, "w", encoding="utf-8") as f:
                f.write(console_log)
            saved.append(console_path)
        except Exception:
            pass

    device_logs = os.path.join(local_mod_dir, "device_logs")
    os.makedirs(device_logs, exist_ok=True)

    for sub in ("logs", "cases", "work_l1"):
        remote = f"{remote_dir.rstrip('/')}/{sub}"
        local = os.path.join(device_logs, sub)
        chk = subprocess.run(
            adb_base + ["shell", f"test -d {remote} && echo OK"],
            capture_output=True,
            text=True,
            timeout=20,
        )
        if "OK" not in (chk.stdout or ""):
            continue
        os.makedirs(local, exist_ok=True)
        pull = subprocess.run(
            adb_base + ["pull", remote + "/.", local + "/"],
            capture_output=True,
            text=True,
            timeout=90,
        )
        if pull.returncode != 0:
            subprocess.run(
                adb_base + ["pull", remote, local],
                capture_output=True,
                text=True,
                timeout=90,
            )
        if os.path.isdir(local) and any(os.scandir(local)):
            saved.append(local)

    find = subprocess.run(
        adb_base
        + [
            "shell",
            f"find {remote_dir} -maxdepth 1 -type d -name 'work_*' 2>/dev/null",
        ],
        capture_output=True,
        text=True,
        timeout=30,
    )
    for line in (find.stdout or "").splitlines():
        remote = line.strip()
        if not remote:
            continue
        name = os.path.basename(remote)
        local = os.path.join(device_logs, name)
        os.makedirs(local, exist_ok=True)
        subprocess.run(
            adb_base + ["pull", remote + "/.", local + "/"],
            capture_output=True,
            text=True,
            timeout=90,
        )
        if os.path.isdir(local) and any(os.scandir(local)):
            saved.append(local)

    manifest = {
        "pulled_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "remote_dir": remote_dir,
        "artifacts": saved,
    }
    mp = os.path.join(local_mod_dir, "log_manifest.json")
    with open(mp, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    saved.append(mp)
    return saved


def _case_desc(case: dict) -> str:
    cid = str(case.get("id") or "")
    return CASE_DESC.get(cid) or str(case.get("name") or cid or "用例")


def _st_zh(st: str) -> str:
    s = (st or "").lower()
    return {
        "pass": "通过",
        "fail": "失败",
        "error": "错误",
        "warn": "待复核",
        "skip": "跳过",
    }.get(s, s or "未知")


def _mm_text(v: Any) -> str:
    s = str(v or "").replace("\n", " ").replace("(", "（").replace(")", "）")
    s = re.sub(r"[\[\]{}]", "", s)
    return s[:80] if s else "—"


def build_test_summary(
    *,
    session_dir: str,
    modules: list[dict],
    script_pool: dict | None = None,
    hal_status: dict | None = None,
    version_verify: dict | None = None,
) -> dict[str, Any]:
    items = []
    for m in modules or []:
        if not isinstance(m, dict):
            continue
        mid = str(m.get("module_id") or "unknown")
        cases_out = []
        for c in m.get("cases") or []:
            if not isinstance(c, dict):
                continue
            cases_out.append(
                {
                    "id": c.get("id"),
                    "name": c.get("name"),
                    "level": c.get("level"),
                    "status": c.get("status"),
                    "status_zh": _st_zh(str(c.get("status") or "")),
                    "content": _case_desc(c),
                    "message": c.get("message") or c.get("detail") or "",
                    "required": c.get("required"),
                }
            )
        log_console = os.path.join(session_dir, "detect", "modules", mid, "console.log")
        pool_log = os.path.join(session_dir, "script_pool_logs", f"{mid}.log")
        if not os.path.isfile(pool_log):
            alt = os.path.join(session_dir, "script_pool_logs", "run.log")
            if mid == "installerhal" and os.path.isfile(alt):
                pool_log = alt
        items.append(
            {
                "module_id": mid,
                "module_name": m.get("module_name") or mid,
                "overall": m.get("overall"),
                "overall_zh": _st_zh(str(m.get("overall") or "")),
                "summary": m.get("summary"),
                "content": f"HAL 模块「{m.get('module_name') or mid}」自检（L1 冒烟 + 可选 L2 四步法）",
                "cases": cases_out,
                "logs": {
                    "console": log_console if os.path.isfile(log_console) else "",
                    "script_pool_log": pool_log if os.path.isfile(pool_log) else "",
                    "device_logs_dir": os.path.join(
                        session_dir, "detect", "modules", mid, "device_logs"
                    ),
                },
            }
        )

    sp = script_pool or {}
    hs = hal_status or {}
    return {
        "schema_version": "1.0",
        "generated_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "session_dir": session_dir,
        "l0": {
            "content": "升级后 L0：开机完成、tombstone、各 HAL 进程存活",
            "passed": hs.get("Passed"),
            "summary": f"Pass={hs.get('PassCount')} Fail={hs.get('FailCount')} Skip={hs.get('SkipCount')}",
        },
        "script_pool": {
            "content": "脚本池：push 模块目录 → 车机执行 run.sh → 拉回 module_result + 日志",
            "passed": sp.get("Passed"),
            "summary": f"Pass={sp.get('PassCount')} Fail={sp.get('FailCount')} Skip={sp.get('SkipCount')}",
        },
        "version_verify": version_verify or {},
        "modules": items,
    }


def build_mindmap_mermaid(summary: dict) -> str:
    lines = ["mindmap", "  root((T2升级后检查))"]
    l0 = summary.get("l0") or {}
    l0_st = "通过" if l0.get("passed") else ("失败" if l0.get("passed") is False else "—")
    lines.append(f"    L0状态检查[{l0_st}]")
    lines.append(f"      内容说明[{_mm_text(l0.get('content'))}]")
    lines.append(f"      结果[{_mm_text(l0.get('summary'))}]")

    sp = summary.get("script_pool") or {}
    sp_st = "通过" if sp.get("passed") else ("失败" if sp.get("passed") is False else "—")
    lines.append(f"    脚本池L1L2[{sp_st}]")
    lines.append(f"      内容说明[{_mm_text(sp.get('content'))}]")
    lines.append(f"      结果[{_mm_text(sp.get('summary'))}]")

    for m in summary.get("modules") or []:
        mid = _mm_text(m.get("module_id"))
        ov = _mm_text(m.get("overall_zh") or m.get("overall"))
        lines.append(f"      模块_{mid}[{mid} {ov}]")
        lines.append(f"        说明[{_mm_text(m.get('content'))}]")
        lines.append(f"        汇总[{_mm_text(m.get('summary'))}]")
        for c in m.get("cases") or []:
            cid = _mm_text(c.get("id"))
            cst = _mm_text(c.get("status_zh") or c.get("status"))
            lines.append(f"        用例_{cid}[{cid} {cst}]")
            lines.append(f"          内容[{_mm_text(c.get('content'))}]")
            msg = c.get("message") or ""
            if msg:
                lines.append(f"          结果[{_mm_text(msg)[:60]}]")
    return "\n".join(lines) + "\n"


def build_mindmap_markdown(summary: dict) -> str:
    mermaid = build_mindmap_mermaid(summary)
    lines = [
        "# T2 升级后检查 · 测试思维导图",
        "",
        f"- 生成时间：{summary.get('generated_at')}",
        f"- 会话目录：`{summary.get('session_dir')}`",
        "",
        "## 思维导图",
        "",
        "```mermaid",
        mermaid.rstrip(),
        "```",
        "",
        "## 测试内容与结果明细",
        "",
    ]
    l0 = summary.get("l0") or {}
    lines += [
        "### L0 状态检查",
        f"- **内容**：{l0.get('content')}",
        f"- **结果**：{l0.get('summary')}（{'通过' if l0.get('passed') else '失败'}）",
        "",
    ]
    sp = summary.get("script_pool") or {}
    lines += [
        "### 脚本池",
        f"- **内容**：{sp.get('content')}",
        f"- **结果**：{sp.get('summary')}",
        "",
    ]
    for m in summary.get("modules") or []:
        lines.append(f"### {m.get('module_name')} (`{m.get('module_id')}`)")
        lines.append(f"- **内容**：{m.get('content')}")
        lines.append(f"- **结果**：{m.get('overall_zh')} · {m.get('summary')}")
        logs = m.get("logs") or {}
        if logs.get("script_pool_log"):
            lines.append(f"- **控制台日志**：`{logs.get('script_pool_log')}`")
        if logs.get("device_logs_dir") and os.path.isdir(str(logs.get("device_logs_dir"))):
            lines.append(f"- **车机日志目录**：`{logs.get('device_logs_dir')}`")
        lines.append("")
        lines.append("| 用例 | 等级 | 内容说明 | 结果 | 说明 |")
        lines.append("|------|------|----------|------|------|")
        for c in m.get("cases") or []:
            lines.append(
                f"| {c.get('id')} | {c.get('level')} | {c.get('content')} | "
                f"{c.get('status_zh')} | {(c.get('message') or '')[:80]} |"
            )
        lines.append("")
    return "\n".join(lines)


def write_session_artifacts(
    session_dir: str,
    modules: list[dict],
    *,
    script_pool: dict | None = None,
    hal_status: dict | None = None,
    version_verify: dict | None = None,
) -> dict[str, Any]:
    detect_dir = os.path.join(session_dir, "detect")
    os.makedirs(detect_dir, exist_ok=True)
    summary = build_test_summary(
        session_dir=session_dir,
        modules=modules,
        script_pool=script_pool,
        hal_status=hal_status,
        version_verify=version_verify,
    )
    md = build_mindmap_markdown(summary)
    mermaid = build_mindmap_mermaid(summary)

    for base in (session_dir, detect_dir):
        with open(os.path.join(base, "test_summary.json"), "w", encoding="utf-8") as f:
            json.dump(summary, f, ensure_ascii=False, indent=2)
        with open(os.path.join(base, "test_mindmap.md"), "w", encoding="utf-8") as f:
            f.write(md)
        with open(os.path.join(base, "test_mindmap.mmd"), "w", encoding="utf-8") as f:
            f.write(mermaid)

    slog = os.path.join(session_dir, "session.log")
    try:
        with open(slog, "a", encoding="utf-8") as f:
            f.write("\n========== 测试思维导图 / 内容与结果 ==========\n")
            f.write(f"test_summary: {os.path.join(session_dir, 'test_summary.json')}\n")
            f.write(f"test_mindmap: {os.path.join(session_dir, 'test_mindmap.md')}\n")
            f.write(f"modules: {len(summary.get('modules') or [])}\n")
            for m in summary.get("modules") or []:
                f.write(
                    f"  - {m.get('module_id')}: {m.get('overall_zh')} {m.get('summary')}\n"
                )
            f.write("================================================\n")
    except Exception:
        pass

    return {
        "test_summary": os.path.join(session_dir, "test_summary.json"),
        "test_mindmap": os.path.join(session_dir, "test_mindmap.md"),
        "test_mindmap_mmd": os.path.join(session_dir, "test_mindmap.mmd"),
        "mermaid": mermaid,
        "summary": summary,
    }


def mindmap_html_section(mermaid: str, summary: dict, *, link_prefix: str = "") -> str:
    def esc(v: Any) -> str:
        return html_mod.escape(str(v if v is not None else "—"))

    rows = []
    for m in summary.get("modules") or []:
        mid = m.get("module_id")
        href = f"{link_prefix}modules/{mid}/module_result.html"
        log_href = f"{link_prefix}modules/{mid}/console.log"
        rows.append(
            "<tr>"
            f"<td><a class='btn-link' href='{esc(href)}'>{esc(mid)}</a></td>"
            f"<td>{esc(m.get('module_name'))}</td>"
            f"<td>{esc(m.get('content'))}</td>"
            f"<td>{esc(m.get('overall_zh'))}</td>"
            f"<td>{esc(m.get('summary'))}</td>"
            f"<td><a class='btn-link' href='{esc(log_href)}'>日志</a></td>"
            "</tr>"
        )

    case_blocks = []
    for m in summary.get("modules") or []:
        mid = m.get("module_id")
        trs = []
        for c in m.get("cases") or []:
            trs.append(
                "<tr>"
                f"<td>{esc(c.get('id'))}</td>"
                f"<td>{esc(c.get('level'))}</td>"
                f"<td>{esc(c.get('content'))}</td>"
                f"<td>{esc(c.get('status_zh'))}</td>"
                f"<td>{esc(c.get('message'))}</td>"
                "</tr>"
            )
        case_blocks.append(
            f"<h3 id='mod-{esc(mid)}'>{esc(m.get('module_name'))} "
            f"<small>({esc(mid)} · {esc(m.get('overall_zh'))})</small></h3>"
            f"<p class='section-summary'>{esc(m.get('content'))}</p>"
            "<table class='data-table'><thead><tr>"
            "<th>用例</th><th>等级</th><th>测试内容说明</th><th>结果</th><th>详情</th>"
            "</tr></thead><tbody>"
            + ("".join(trs) or "<tr><td colspan=5>无用例</td></tr>")
            + "</tbody></table>"
        )

    mermaid_esc = mermaid.replace("</", "<\\/")
    return f"""
    <section class="section" id="mindmap">
      <h2>测试思维导图</h2>
      <p class="section-summary">展示本次检查结构、各模块测试内容与结果。源文件：
        <a class="btn-link" href="{link_prefix}test_mindmap.md">test_mindmap.md</a> ·
        <a class="btn-link" href="{link_prefix}test_summary.json">test_summary.json</a>
      </p>
      <pre class="mermaid">
{mermaid_esc}
      </pre>
    </section>

    <section class="section" id="content-result">
      <h2>测试内容说明与结果</h2>
      <p class="section-summary">按模块汇总：测什么、结论如何、日志在哪。</p>
      <table class="data-table">
        <thead>
          <tr><th>模块</th><th>名称</th><th>测试内容</th><th>结果</th><th>摘要</th><th>日志</th></tr>
        </thead>
        <tbody>
          {''.join(rows) or '<tr><td colspan=6>无模块结果</td></tr>'}
        </tbody>
      </table>
      {''.join(case_blocks)}
    </section>
"""
