# -*- coding: utf-8 -*-
"""
T2 测试报告：对齐 BMC_ATF detect_report.py 的 HTML 版式（冒烟报告风格）。
在 HAL 脚本池 module_result 之外，汇总版本校验、L0、脚本池与升级包信息。

固定输出：
  <session>/detect_result.html
  <session>/detect/detect_result.html
"""

from __future__ import annotations

import html
import json
import os
import sys
from datetime import datetime
from typing import Any


def _esc(v: Any) -> str:
    if v is None:
        return "—"
    return html.escape(str(v))


def _status_label(st: str) -> tuple[str, str]:
    s = (st or "").lower()
    if s in ("pass", "passed", "success", "ok", "true"):
        return "pass", "通过"
    if s in ("fail", "failed", "error", "false"):
        return "fail", "失败"
    if s in ("warn", "warning", "review"):
        return "warn", "待复核"
    if s in ("skip", "skipped", "n/a", "na"):
        return "skip", "未执行/跳过"
    return "skip", "未执行/跳过"


def _module_passed(m: dict) -> bool | None:
    """兼容 HalStatusChecker JSON：Result=PASS/FAIL/SKIP 或 Passed/Status。"""
    if not isinstance(m, dict):
        return None
    if "Passed" in m and m.get("Passed") is not None:
        return bool(m.get("Passed"))
    for key in ("Result", "Status", "status", "result"):
        v = m.get(key)
        if v is None:
            continue
        s = str(v).strip().lower()
        if s in ("pass", "passed", "ok", "success"):
            return True
        if s in ("fail", "failed", "error"):
            return False
        if s in ("skip", "skipped"):
            return None
    return None


def _ota_path_display(package_path: str | None) -> str:
    p = (package_path or "").strip()
    if not p or p in ("—", "-", "N/A", "n/a"):
        return "非ota升级"
    return p


def _load_json(path: str) -> dict | None:
    if not path or not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else None
    except Exception:
        return None


def _resolve_pc_info(scripts_dir: str = "") -> tuple[str, str]:
    """从 t2_atf_config → bmc config.json 的 pc 段读取 pc_id / 名称。"""
    pc_id, pc_name = "", ""
    candidates = []
    if scripts_dir:
        atf = os.path.join(scripts_dir, "t2_atf_config.json")
        candidates.append(atf)
        bmc_default = os.path.normpath(
            os.path.join(scripts_dir, "..", "BMC_ATF_auto-exec-controller", "config.json")
        )
        candidates.append(bmc_default)
    for path in candidates:
        data = _load_json(path)
        if not data:
            continue
        if "pc" in data and isinstance(data["pc"], dict):
            pc = data["pc"]
            pc_id = str(pc.get("pc_id") or pc_id or "")
            pc_name = str(pc.get("name") or pc.get("alias") or pc_name or "")
            if pc_id or pc_name:
                break
        bmc_rel = data.get("bmc_config_path")
        if bmc_rel and scripts_dir:
            bmc_path = bmc_rel if os.path.isabs(bmc_rel) else os.path.normpath(
                os.path.join(scripts_dir, bmc_rel)
            )
            bmc = _load_json(bmc_path)
            if bmc and isinstance(bmc.get("pc"), dict):
                pc = bmc["pc"]
                pc_id = str(pc.get("pc_id") or pc_id or "")
                pc_name = str(pc.get("name") or pc.get("alias") or pc_name or "")
                break
    if not pc_name:
        try:
            import socket
            pc_name = socket.gethostname()
        except Exception:
            pc_name = ""
    return pc_id, pc_name


def build_bmc_compatible_result_data(
    *,
    version_verify: dict | None = None,
    hal_status: dict | None = None,
    script_pool: dict | None = None,
    modules: list[dict] | None = None,
    package_path: str = "",
    progress: dict | None = None,
    device_info: dict | None = None,
) -> dict:
    """把 T2 产物映射为接近 BMC result_data 的字典，供冒烟报告使用。"""
    vv = version_verify or {}
    hs = hal_status or {}
    sp = script_pool or {}
    di = device_info or {}
    data: dict[str, Any] = {
        "package_path": _ota_path_display(package_path or di.get("package_path") or vv.get("PackagePath")),
        "progress": progress or {},
        "t2_modules": modules or [],
        "t2_script_items": sp.get("Items") or [],
    }

    passed = vv.get("Passed")
    if passed is None and vv:
        passed = True
    data["version_detect"] = bool(passed) if vv else None
    if vv and not passed:
        data["version_detect_error"] = vv.get("Message") or vv.get("Error") or "版本校验失败"

    data["soc"] = (
        di.get("soc")
        or vv.get("ActualBuildIncremental")
        or vv.get("SocVersion")
        or vv.get("soc")
        or vv.get("BuildIncremental")
        or ""
    )
    data["mcu"] = (
        di.get("mcu")
        or vv.get("McuVersion")
        or vv.get("mcu")
        or ""
    )
    if not data["soc"] and isinstance(vv.get("Items"), list):
        for it in vv["Items"]:
            if not isinstance(it, dict):
                continue
            name = str(it.get("Name") or it.get("name") or "")
            if "incremental" in name.lower() or name in ("SOC版本", "ro.build.version.incremental"):
                data["soc"] = str(it.get("Actual") or it.get("actual") or data["soc"])

    data["hal_l0_passed"] = hs.get("Passed") if hs else None
    data["hal_l0_pass_count"] = hs.get("PassCount")
    data["hal_l0_fail_count"] = hs.get("FailCount")
    data["hal_l0_skip_count"] = hs.get("SkipCount")
    data["hal_l0_modules"] = hs.get("Modules") or hs.get("Results") or []
    data["hal_l0_global"] = hs.get("GlobalChecks") or []

    data["script_pool_enabled"] = bool(sp.get("enabled", True)) if sp else False
    data["script_pool_passed"] = sp.get("Passed") if sp else None
    data["script_pool_pass_count"] = sp.get("PassCount")
    data["script_pool_fail_count"] = sp.get("FailCount")
    data["script_pool_skip_count"] = sp.get("SkipCount")
    return data


def _build_t2_rows(data: dict) -> list[dict]:
    rows = []
    tid = 0

    def add(category, name, expect, actual, status, remark=""):
        nonlocal tid
        tid += 1
        st, label = _status_label(status)
        rows.append({
            "id": f"T{tid:02d}",
            "category": category,
            "name": name,
            "expect": expect,
            "actual": actual,
            "status": st,
            "label": label,
            "remark": remark,
        })

    pkg = data.get("package_path") or "非ota升级"
    soc = data.get("soc") or "—"
    mcu = data.get("mcu") or "—"
    add(
        "升级包",
        "OTA 版本路径",
        "OTA 升级记录路径；非升级场景标注「非ota升级」",
        _esc(pkg),
        "pass" if pkg else "warn",
        f"SOC: {_esc(soc)} · MCU: {_esc(mcu)}",
    )

    vd = data.get("version_detect")
    if vd is None:
        add(
            "固件版本",
            "SOC / MCU 版本",
            "记录当前 SOC、MCU 版本号",
            f"SOC: {_esc(soc)} | MCU: {_esc(mcu)}",
            "pass" if (soc and soc != "—") else "warn",
            "本次未跑 OTA 版本校验，版本取自车机 getprop" if (soc and soc != "—") else "未采集到版本",
        )
    else:
        add(
            "固件版本",
            "SOC / 版本校验",
            "版本校验通过",
            f"SOC: {_esc(soc)} | MCU: {_esc(mcu)}",
            "pass" if vd else "fail",
            _esc(data.get("version_detect_error") or ""),
        )

    for line in data.get("hal_l0_global") or []:
        sline = str(line)
        if sline.upper().startswith("FAIL"):
            st = "fail"
        elif sline.upper().startswith("PASS"):
            st = "pass"
        else:
            st = "warn"
        add("系统状态", sline[5:].strip() if len(sline) > 5 else sline, "系统态正常", _esc(sline), st, "")

    if data.get("hal_l0_passed") is None and not data.get("hal_l0_modules"):
        add("HAL L0", "服务存活检查", "名单内 HAL 进程/服务存活", "未执行", "skip", "")
    else:
        pc = data.get("hal_l0_pass_count")
        fc = data.get("hal_l0_fail_count")
        sc = data.get("hal_l0_skip_count")
        add(
            "HAL L0",
            "服务存活检查",
            "Required 模块全部 Pass",
            f"Pass={pc} Fail={fc} Skip={sc}",
            "pass" if int(fc or 0) == 0 else "fail",
            "",
        )
        for m in data.get("hal_l0_modules") or []:
            if not isinstance(m, dict):
                continue
            mid = (
                m.get("ModuleName") or m.get("ModuleId") or m.get("Id")
                or m.get("id") or m.get("Name") or "module"
            )
            ok = _module_passed(m)
            result = str(m.get("Result") or m.get("Status") or "").upper()
            if ok is None and result == "SKIP":
                st = "skip"
            elif ok is True:
                st = "pass"
            elif ok is False:
                st = "fail"
            else:
                st = "skip"
            add(
                "HAL L0",
                str(mid),
                "进程/服务存活",
                _esc(m.get("Message") or m.get("Detail") or result or ("Pass" if ok else "—")),
                st,
                "",
            )

    items = data.get("t2_script_items") or []
    modules = data.get("t2_modules") or []
    if not items and not modules:
        add("脚本池", "HAL 功能自检", "按配置执行脚本池并产出 module_result", "未执行", "skip", "")
    for it in items:
        if not isinstance(it, dict):
            continue
        if it.get("skipped"):
            st = "skip"
        else:
            st = "pass" if it.get("passed") else "fail"
        add(
            "脚本池",
            it.get("name") or it.get("id") or "script",
            "脚本池格式自检（run.sh / module_result.json）",
            f"exit={it.get('exit_code')} dur={it.get('duration_sec')}s",
            st,
            _esc(it.get("error") or ""),
        )
    for mod in modules:
        if not isinstance(mod, dict):
            continue
        mid = mod.get("module_id") or "module"
        overall = mod.get("overall") or "skip"
        add(
            "脚本池模块",
            f"{mod.get('module_name') or mid} ({mid})",
            "module_result.overall=pass",
            _esc(mod.get("summary") or overall),
            overall,
            f'<a href="detect/modules/{_esc(mid)}/module_result.html">详情</a>',
        )
        for case in mod.get("cases") or []:
            if not isinstance(case, dict):
                continue
            add(
                "脚本池用例",
                f"{mid}/{case.get('id') or case.get('name')}",
                "用例 Pass",
                _esc(case.get("message") or case.get("summary") or case.get("status")),
                case.get("status") or "skip",
                _esc(case.get("level") or ""),
            )
    return rows


def _overall_verdict(rows: list[dict]) -> tuple[str, str, str]:
    failed = sum(1 for r in rows if r["status"] == "fail")
    warned = sum(1 for r in rows if r["status"] == "warn")
    if failed:
        return "fail", "不通过", "存在失败项，请逐表复查"
    if warned:
        return "warn", "有条件通过", "存在待复核或告警项"
    if not rows or all(r["status"] == "skip" for r in rows):
        return "skip", "未执行", "无有效测试项"
    return "pass", "通过", "全部必测项通过"


def _badge(status: str, label: str = "") -> str:
    st, default_label = _status_label(status)
    return f'<span class="badge badge-{st}">{_esc(label or default_label)}</span>'


def _table_html(headers: list[str], body_rows: list[list[str]]) -> str:
    th = "".join(f"<th>{h}</th>" for h in headers)
    trs = []
    for cells in body_rows:
        tds = "".join(f"<td>{c}</td>" for c in cells)
        trs.append(f"<tr>{tds}</tr>")
    body = "\n".join(trs) if trs else f'<tr><td colspan="{len(headers)}">无数据</td></tr>'
    return f"""<table class="data-table">
  <thead><tr>{th}</tr></thead>
  <tbody>
{body}
  </tbody>
</table>"""


def _build_report_sections(result_data: dict, *, link_prefix: str = "") -> tuple[list[dict], list[dict]]:
    """
    返回 (overview_rows, sections)。
    overview_rows: 总览表行（对齐 BMC「2. 测试结果明细」）
    sections: [{title, summary, headers, rows}] 每模块/类别一张表，全部展开。
    """
    overview: list[dict] = []
    sections: list[dict] = []
    tid = 0

    def ov(category, name, expect, actual, status, remark=""):
        nonlocal tid
        tid += 1
        st, label = _status_label(status)
        overview.append({
            "id": f"T{tid:02d}",
            "category": category,
            "name": name,
            "expect": expect,
            "actual": actual,
            "status": st,
            "label": label,
            "remark": remark,
        })

    pkg = result_data.get("package_path") or "非ota升级"
    soc = result_data.get("soc") or "—"
    mcu = result_data.get("mcu") or "—"
    ov("升级包", "OTA 版本路径", "记录路径或标注非ota升级", _esc(pkg), "pass", f"SOC={_esc(soc)} MCU={_esc(mcu)}")

    vd = result_data.get("version_detect")
    if vd is None:
        ov("固件版本", "SOC / MCU 版本", "记录版本号", f"SOC: {_esc(soc)} | MCU: {_esc(mcu)}",
           "pass" if soc and soc != "—" else "warn", "未跑 OTA 版本校验" if soc else "未采集")
    else:
        ov("固件版本", "SOC / 版本校验", "版本校验通过",
           f"SOC: {_esc(soc)} | MCU: {_esc(mcu)}",
           "pass" if vd else "fail", _esc(result_data.get("version_detect_error") or ""))

    # —— 系统状态表 ——
    glob_rows = []
    for i, line in enumerate(result_data.get("hal_l0_global") or [], 1):
        sline = str(line)
        if sline.upper().startswith("FAIL"):
            st = "fail"
        elif sline.upper().startswith("PASS"):
            st = "pass"
        else:
            st = "warn"
        name = sline[5:].strip() if len(sline) > 5 else sline
        glob_rows.append([
            f"G{i:02d}",
            _esc(name),
            "系统态正常",
            _esc(sline),
            _badge(st),
            "—",
        ])
    if glob_rows:
        fail_n = sum(1 for r in glob_rows if "badge-fail" in r[4])
        ov("系统状态", "全局系统检查", "boot/tombstone 等正常",
           f"项数={len(glob_rows)} Fail={fail_n}", "fail" if fail_n else "pass", "")
        sections.append({
            "title": "3. 系统状态明细",
            "summary": f"共 {len(glob_rows)} 项",
            "headers": ["编号", "检查项", "期望", "实际", "结论", "备注"],
            "rows": glob_rows,
        })

    # —— HAL L0 表 ——
    l0_mods = result_data.get("hal_l0_modules") or []
    l0_rows = []
    for m in l0_mods:
        if not isinstance(m, dict):
            continue
        mid = (
            m.get("ModuleName") or m.get("ModuleId") or m.get("Id")
            or m.get("id") or m.get("Name") or "module"
        )
        ok = _module_passed(m)
        result = str(m.get("Result") or m.get("Status") or "").upper()
        if ok is None and result == "SKIP":
            st = "skip"
        elif ok is True:
            st = "pass"
        elif ok is False:
            st = "fail"
        else:
            st = "skip"
        req = m.get("Required")
        req_s = "是" if req is True else ("否" if req is False else "—")
        l0_rows.append([
            _esc(mid),
            _esc(m.get("ModuleId") or m.get("id") or ""),
            req_s,
            "进程/服务存活",
            _esc(m.get("Detail") or m.get("Message") or result or "—"),
            _badge(st),
        ])
    if l0_rows or result_data.get("hal_l0_passed") is not None:
        pc = result_data.get("hal_l0_pass_count")
        fc = result_data.get("hal_l0_fail_count")
        sc = result_data.get("hal_l0_skip_count")
        ov("HAL L0", "服务存活检查", "Required 模块全部 Pass",
           f"Pass={pc} Fail={fc} Skip={sc}",
           "pass" if int(fc or 0) == 0 else "fail", "")
        sections.append({
            "title": "4. HAL L0 服务存活明细",
            "summary": f"Pass={pc} Fail={fc} Skip={sc}",
            "headers": ["模块", "ModuleId", "必测", "期望", "实际", "结论"],
            "rows": l0_rows or [["—", "—", "—", "未执行", "—", _badge("skip")]],
        })

    # —— 脚本池总览 + 每模块一张表 ——
    items = result_data.get("t2_script_items") or []
    modules = result_data.get("t2_modules") or []
    sec_no = 5

    if items:
        sp_rows = []
        for it in items:
            if not isinstance(it, dict):
                continue
            if it.get("skipped"):
                st = "skip"
            else:
                st = "pass" if it.get("passed") else "fail"
            sp_rows.append([
                _esc(it.get("id") or ""),
                _esc(it.get("name") or it.get("id") or "script"),
                _esc(it.get("kind") or "hal_module"),
                "run.sh 执行成功并产出 module_result",
                f"exit={_esc(it.get('exit_code'))} · {_esc(it.get('duration_sec'))}s",
                _badge(st),
                _esc(it.get("error") or "—"),
            ])
            ov("脚本池", it.get("name") or it.get("id") or "script",
               "脚本池自检通过",
               f"exit={it.get('exit_code')}", st, _esc(it.get("error") or ""))
        sections.append({
            "title": f"{sec_no}. 脚本池执行总览",
            "summary": f"共 {len(sp_rows)} 项",
            "headers": ["ID", "名称", "类型", "期望", "实际", "结论", "备注"],
            "rows": sp_rows,
        })
        sec_no += 1
    elif not modules:
        ov("脚本池", "HAL 功能自检", "执行脚本池", "未执行", "skip", "")
        sections.append({
            "title": f"{sec_no}. 脚本池执行总览",
            "summary": "未执行",
            "headers": ["ID", "名称", "类型", "期望", "实际", "结论", "备注"],
            "rows": [["—", "—", "—", "未执行", "—", _badge("skip"), "—"]],
        })
        sec_no += 1

    for mod in modules:
        if not isinstance(mod, dict):
            continue
        mid = str(mod.get("module_id") or "module")
        mname = mod.get("module_name") or mid
        overall = mod.get("overall") or "skip"
        detail_href = f"{link_prefix}modules/{_esc(mid)}/module_result.html"
        ov("脚本池模块", f"{mname} ({mid})", "module_result.overall=pass",
           _esc(mod.get("summary") or overall), overall,
           f'<a class="btn-link" href="{detail_href}">模块报告</a>')

        case_rows = []
        for i, case in enumerate(mod.get("cases") or [], 1):
            if not isinstance(case, dict):
                continue
            st = case.get("status") or "skip"
            req = case.get("required")
            req_s = "是" if req is True else ("否" if req is False else "—")
            case_rows.append([
                f"C{i:02d}",
                _esc(case.get("id") or ""),
                _esc(case.get("name") or case.get("id") or ""),
                _esc(case.get("level") or "—"),
                req_s,
                "Pass",
                _esc(case.get("message") or case.get("summary") or case.get("status") or "—"),
                _badge(st),
            ])
        # 无 cases 时仍出空表，保证「每个模块一张表」可见
        sections.append({
            "title": f"{sec_no}. 模块明细 · {mname}（{mid}）",
            "summary": _esc(mod.get("summary") or overall)
            + f' · <a class="btn-link" href="{detail_href}">查看 module_result.html</a>',
            "headers": ["编号", "用例ID", "用例名称", "等级", "必测", "期望", "实际说明", "结论"],
            "rows": case_rows or [["—", "—", "无用例", "—", "—", "—", "—", _badge("skip")]],
        })
        sec_no += 1

    # —— 附件 ——
    sections.append({
        "title": f"{sec_no}. 附件清单",
        "summary": "会话目录产物",
        "headers": ["文件", "说明", "生成条件"],
        "rows": [
            ["detect_result.html", "冒烟/升级后检查汇总报告", "始终生成"],
            ["hal_status.json", "L0 结构化结果", "执行 HAL 状态检查时"],
            ["script_results.json", "脚本池执行汇总", "执行脚本池时"],
            ["device_info.json", "PC / SOC / MCU / OTA 路径", "生成报告时"],
            ["detect/modules/*/module_result.json", "各模块用例明细", "模块自检产出时"],
            ["session.log", "完整运行日志", "始终生成"],
        ],
    })

    return overview, sections


def render_bmc_style_report(
    result_data: dict,
    *,
    pc_id: str = "",
    pc_name: str = "",
    soc_version: str = "",
    mcu_version: str = "",
    parent_dir_name: str = "",
    generated_at: datetime | None = None,
    link_prefix: str = "detect/",
    mindmap_section_html: str = "",
) -> str:
    """版式对齐 BMC detect_result：总览表 + 每模块独立明细表（全部展开）。"""
    overview, sections = _build_report_sections(result_data, link_prefix=link_prefix)
    # 兼容旧调用：也算一遍扁平 rows 做总结论
    flat_for_verdict = overview[:]
    for sec in sections:
        for row in sec.get("rows") or []:
            # 从 badge class 推断
            badge = row[-1] if row else ""
            st = "skip"
            if "badge-fail" in badge:
                st = "fail"
            elif "badge-pass" in badge:
                st = "pass"
            elif "badge-warn" in badge:
                st = "warn"
            flat_for_verdict.append({"status": st})

    overall_code, overall_label, overall_desc = _overall_verdict(flat_for_verdict)
    now = generated_at or datetime.now()
    passed = sum(1 for r in overview if r["status"] == "pass")
    failed = sum(1 for r in overview if r["status"] == "fail")
    warned = sum(1 for r in overview if r["status"] == "warn")
    skipped = sum(1 for r in overview if r["status"] == "skip")
    total = len(overview)

    ov_body = []
    for r in overview:
        ov_body.append([
            f'<td class="col-id">{_esc(r["id"])}</td>',
            f'<td>{_esc(r["category"])}</td>',
            f'<td class="col-name">{_esc(r["name"])}</td>',
            f'<td class="col-expect">{_esc(r["expect"])}</td>',
            f'<td class="col-actual">{r["actual"]}</td>',
            f'<td>{_badge(r["status"], r["label"])}</td>',
            f'<td class="col-remark">{r["remark"] or "—"}</td>',
        ])
    # _table_html expects plain cells; build overview table manually
    ov_trs = []
    for cells in ov_body:
        ov_trs.append("<tr>" + "".join(cells) + "</tr>")
    overview_table = f"""<table class="data-table">
  <thead>
    <tr>
      <th>编号</th><th>分类</th><th>测试项</th><th>预期结果</th><th>实际结果</th><th>结论</th><th>备注</th>
    </tr>
  </thead>
  <tbody>
{chr(10).join(ov_trs) if ov_trs else '<tr><td colspan="7">无数据</td></tr>'}
  </tbody>
</table>"""

    section_html = []
    for sec in sections:
        section_html.append(f"""
    <section class="section">
      <h2>{_esc(sec['title'])}</h2>
      <p class="section-summary">{sec.get('summary') or ''}</p>
      {_table_html(sec['headers'], sec['rows'])}
    </section>""")

    pkg = _esc(_ota_path_display(result_data.get("package_path")))
    soc_disp = _esc(soc_version or result_data.get("soc") or "—")
    mcu_disp = _esc(mcu_version or result_data.get("mcu") or "—")
    pc_id_disp = _esc(pc_id or "—")
    pc_name_disp = _esc(pc_name or "—")
    gen_time = now.strftime("%Y-%m-%d %H:%M:%S")
    overall_cls = f"overall-{overall_code}"
    mindmap_block = mindmap_section_html or ""

    return f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <title>T2 升级后检查报告</title>
  <script type="module">
    import mermaid from 'https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.esm.min.mjs';
    mermaid.initialize({{ startOnLoad: true, theme: 'default', securityLevel: 'loose' }});
  </script>
  <style>
    :root {{
      --bg: #f4f6f9;
      --card: #ffffff;
      --text: #1a1a2e;
      --muted: #5c6370;
      --border: #e2e6ed;
      --primary: #1565c0;
      --pass: #2e7d32;
      --pass-bg: #e8f5e9;
      --fail: #c62828;
      --fail-bg: #ffebee;
      --warn: #ef6c00;
      --warn-bg: #fff3e0;
      --skip: #757575;
      --skip-bg: #f5f5f5;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      padding: 32px 24px 48px;
      font-family: "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
      background: var(--bg);
      color: var(--text);
      line-height: 1.5;
    }}
    .container {{ max-width: 1180px; margin: 0 auto; }}
    .header {{
      background: linear-gradient(135deg, #0d47a1 0%, #1565c0 55%, #1976d2 100%);
      color: #fff;
      border-radius: 12px;
      padding: 28px 32px;
      margin-bottom: 24px;
      box-shadow: 0 4px 20px rgba(21, 101, 192, 0.25);
    }}
    .header h1 {{ margin: 0 0 8px; font-size: 26px; font-weight: 600; }}
    .header .subtitle {{ opacity: 0.9; font-size: 14px; }}
    .verdict-banner {{
      display: flex; align-items: center; gap: 16px;
      background: var(--card); border-radius: 12px;
      padding: 20px 28px; margin-bottom: 24px;
      border-left: 6px solid var(--primary);
      box-shadow: 0 2px 8px rgba(0,0,0,0.06);
    }}
    .verdict-banner.overall-pass {{ border-left-color: var(--pass); }}
    .verdict-banner.overall-fail {{ border-left-color: var(--fail); }}
    .verdict-banner.overall-warn {{ border-left-color: var(--warn); }}
    .verdict-banner.overall-skip {{ border-left-color: var(--skip); }}
    .verdict-label {{ font-size: 13px; color: var(--muted); letter-spacing: 0.05em; }}
    .verdict-value {{ font-size: 28px; font-weight: 700; }}
    .verdict-banner.overall-pass .verdict-value {{ color: var(--pass); }}
    .verdict-banner.overall-fail .verdict-value {{ color: var(--fail); }}
    .verdict-banner.overall-warn .verdict-value {{ color: var(--warn); }}
    .verdict-desc {{ color: var(--muted); font-size: 14px; margin-top: 4px; }}
    .stats {{
      display: grid; grid-template-columns: repeat(5, 1fr); gap: 12px; margin-bottom: 24px;
    }}
    @media (max-width: 768px) {{ .stats {{ grid-template-columns: repeat(2, 1fr); }} }}
    .stat-card {{
      background: var(--card); border-radius: 10px; padding: 16px; text-align: center;
      box-shadow: 0 2px 8px rgba(0,0,0,0.05);
    }}
    .stat-card .num {{ font-size: 28px; font-weight: 700; }}
    .stat-card .lbl {{ font-size: 12px; color: var(--muted); margin-top: 4px; }}
    .stat-card.pass .num {{ color: var(--pass); }}
    .stat-card.fail .num {{ color: var(--fail); }}
    .stat-card.warn .num {{ color: var(--warn); }}
    .section {{
      background: var(--card); border-radius: 12px; padding: 24px 28px;
      margin-bottom: 20px; box-shadow: 0 2px 8px rgba(0,0,0,0.05);
    }}
    .section h2 {{
      margin: 0 0 10px; font-size: 17px; color: var(--primary);
      border-bottom: 2px solid var(--border); padding-bottom: 10px;
    }}
    .section h3 {{ margin: 20px 0 8px; font-size: 15px; color: var(--text); }}
    .section-summary {{ margin: 0 0 14px; color: var(--muted); font-size: 13px; }}
    .meta-grid {{
      display: grid; grid-template-columns: repeat(2, 1fr); gap: 12px 32px; font-size: 14px;
    }}
    @media (max-width: 600px) {{ .meta-grid {{ grid-template-columns: 1fr; }} }}
    .meta-item label {{ display: block; color: var(--muted); font-size: 12px; margin-bottom: 2px; }}
    .meta-item span {{ font-weight: 500; word-break: break-all; }}
    table.data-table {{
      width: 100%; border-collapse: collapse; font-size: 13px;
    }}
    table.data-table th, table.data-table td {{
      border-bottom: 1px solid var(--border); padding: 10px 8px;
      text-align: left; vertical-align: top;
    }}
    table.data-table th {{
      color: var(--muted); font-weight: 600; background: #fafbfd; white-space: nowrap;
    }}
    .col-id {{ width: 56px; white-space: nowrap; }}
    .col-name {{ min-width: 140px; }}
    .col-expect, .col-actual, .col-remark {{ word-break: break-word; }}
    .badge {{
      display: inline-block; padding: 2px 10px; border-radius: 999px;
      font-size: 12px; font-weight: 600; white-space: nowrap;
    }}
    .badge-pass {{ background: var(--pass-bg); color: var(--pass); }}
    .badge-fail {{ background: var(--fail-bg); color: var(--fail); }}
    .badge-warn {{ background: var(--warn-bg); color: var(--warn); }}
    .badge-skip {{ background: var(--skip-bg); color: var(--skip); }}
    .btn-link {{
      color: var(--primary); text-decoration: none; font-weight: 600;
    }}
    .btn-link:hover {{ text-decoration: underline; }}
    .toc {{ font-size: 13px; color: var(--muted); margin: 0 0 8px; }}
    .toc a {{ color: var(--primary); margin-right: 12px; text-decoration: none; }}
    .footer {{ text-align: center; color: var(--muted); font-size: 12px; margin-top: 24px; }}
    pre.mermaid {{
      background: #fafbfd; border: 1px solid var(--border); border-radius: 8px;
      padding: 16px; overflow: auto;
    }}
  </style>
</head>
<body>
  <div class="container">
    <header class="header">
      <h1>T2 升级后检查报告</h1>
      <p class="subtitle">Smoke Test Report · 思维导图 / 测试内容说明 / 测试结果 · HTML</p>
    </header>

    <div class="verdict-banner {overall_cls}">
      <div>
        <div class="verdict-label">总体结论</div>
        <div class="verdict-value">{_esc(overall_label)}</div>
        <div class="verdict-desc">{_esc(overall_desc)}</div>
      </div>
    </div>

    <div class="stats">
      <div class="stat-card"><div class="num">{total}</div><div class="lbl">总览项</div></div>
      <div class="stat-card pass"><div class="num">{passed}</div><div class="lbl">通过</div></div>
      <div class="stat-card fail"><div class="num">{failed}</div><div class="lbl">失败</div></div>
      <div class="stat-card warn"><div class="num">{warned}</div><div class="lbl">待复核</div></div>
      <div class="stat-card"><div class="num">{skipped}</div><div class="lbl">未执行</div></div>
    </div>

    <p class="toc">
      <a href="#mindmap">思维导图</a>
      <a href="#content-result">内容与结果</a>
      <a href="#overview">总览</a>
    </p>

    <section class="section">
      <h2>1. 测试概要</h2>
      <div class="meta-grid">
        <div class="meta-item"><label>报告生成时间</label><span>{gen_time}</span></div>
        <div class="meta-item"><label>任务类型</label><span>升级后检查 / HAL 自检</span></div>
        <div class="meta-item"><label>测试 PC</label><span>{pc_name_disp}</span></div>
        <div class="meta-item"><label>PC ID</label><span>{pc_id_disp}</span></div>
        <div class="meta-item"><label>OTA 版本路径</label><span>{pkg}</span></div>
        <div class="meta-item"><label>构建目录</label><span>{_esc(parent_dir_name or "—")}</span></div>
        <div class="meta-item"><label>SOC 版本</label><span>{soc_disp}</span></div>
        <div class="meta-item"><label>MCU 版本</label><span>{mcu_disp}</span></div>
      </div>
    </section>

    {mindmap_block}

    <section class="section" id="overview">
      <h2>2. 测试结果明细（总览）</h2>
      <p class="section-summary">高层汇总；各模块完整用例见下方分表（全部展开，不折叠）。</p>
      {overview_table}
    </section>

    {''.join(section_html)}

    <div class="footer">
      ATF 自动化测试控制 · detect_result.html · T2 脚本池 / SocOtaUpgrade · {gen_time}
    </div>
  </div>
</body>
</html>"""


def write_module_html(module: dict, out_path: str) -> None:
    mid = module.get("module_id") or "module"
    cases = module.get("cases") or []
    rows = []
    for c in cases:
        if not isinstance(c, dict):
            continue
        st, label = _status_label(c.get("status"))
        rows.append(
            f"<tr><td>{_esc(c.get('id'))}</td><td>{_esc(c.get('name'))}</td>"
            f"<td>{_esc(c.get('level'))}</td>"
            f"<td><span class='badge badge-{st}'>{label}</span></td>"
            f"<td>{_esc(c.get('message') or c.get('summary'))}</td></tr>"
        )
    # 嵌入控制台日志摘要（同目录 console.log）
    log_excerpt = ""
    console = os.path.join(os.path.dirname(out_path), "console.log")
    if os.path.isfile(console):
        try:
            raw = open(console, "r", encoding="utf-8", errors="replace").read()
            log_excerpt = raw[-4000:]
        except Exception:
            log_excerpt = ""
    log_block = ""
    if log_excerpt:
        log_block = (
            "<h2>测试日志（截取）</h2>"
            f"<p><a href='console.log'>console.log</a> · "
            f"<a href='device_logs/'>device_logs/</a></p>"
            f"<pre style='white-space:pre-wrap;background:#fafbfd;border:1px solid #e2e6ed;"
            f"padding:12px;border-radius:8px;max-height:420px;overflow:auto'>"
            f"{_esc(log_excerpt)}</pre>"
        )
    doc = f"""<!DOCTYPE html><html lang="zh-CN"><head><meta charset="UTF-8"/>
<title>{_esc(mid)}</title>
<style>
body{{font-family:"Segoe UI","Microsoft YaHei",sans-serif;margin:24px;background:#f4f6f9}}
.card{{background:#fff;border-radius:10px;padding:20px;border:1px solid #e2e6ed}}
.badge{{padding:2px 10px;border-radius:999px;font-size:12px}}
.badge-pass{{background:#e8f5e9;color:#2e7d32}}
.badge-fail{{background:#ffebee;color:#c62828}}
.badge-warn{{background:#fff3e0;color:#ef6c00}}
.badge-skip{{background:#f5f5f5;color:#757575}}
table{{width:100%;border-collapse:collapse}} th,td{{border-bottom:1px solid #e2e6ed;padding:8px;text-align:left}}
</style></head><body><div class="card">
<h1>{_esc(module.get('module_name') or mid)}</h1>
<p>overall={_esc(module.get('overall'))} · {_esc(module.get('summary'))}</p>
<table><thead><tr><th>ID</th><th>名称</th><th>等级</th><th>结果</th><th>说明</th></tr></thead>
<tbody>{''.join(rows) or '<tr><td colspan=5>无用例</td></tr>'}</tbody></table>
{log_block}
</div></body></html>"""
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(doc)


def _collect_modules_from_session(session_dir: str, modules: list[dict] | None) -> list[dict]:
    out = list(modules or [])
    known = {str(m.get("module_id")) for m in out if isinstance(m, dict)}
    mod_root = os.path.join(session_dir, "detect", "modules")
    if not os.path.isdir(mod_root):
        return out
    for name in sorted(os.listdir(mod_root)):
        if name in known:
            continue
        jp = os.path.join(mod_root, name, "module_result.json")
        data = _load_json(jp)
        if data:
            out.append(data)
    return out


def build_detect_dir(
    session_or_detect_dir: str,
    modules: list[dict],
    *,
    pc_id: str = "",
    pc_name: str = "",
    soc_version: str = "",
    mcu_version: str = "",
    parent_dir_name: str = "",
    version_verify: dict | None = None,
    hal_status: dict | None = None,
    script_pool: dict | None = None,
    package_path: str = "",
    progress: dict | None = None,
    device_info: dict | None = None,
    scripts_dir: str = "",
) -> str:
    """
    固定在会话目录生成 BMC 风格报告：
      - <session>/detect/detect_result.html
      - <session>/detect_result.html
    """
    base = session_or_detect_dir
    if os.path.basename(base.rstrip("\\/")) == "detect":
        detect_dir = base
        session_dir = os.path.dirname(base)
    else:
        session_dir = base
        detect_dir = os.path.join(base, "detect")
    os.makedirs(detect_dir, exist_ok=True)

    di = dict(device_info or {})
    session_di = _load_json(os.path.join(session_dir, "device_info.json")) or {}
    for k, v in session_di.items():
        di.setdefault(k, v)

    if not pc_id or not pc_name:
        rid, rname = _resolve_pc_info(scripts_dir or os.path.dirname(os.path.dirname(session_dir)))
        pc_id = pc_id or rid or di.get("pc_id") or ""
        pc_name = pc_name or rname or di.get("pc_name") or ""

    pkg = package_path or di.get("package_path") or ""
    if not version_verify:
        version_verify = _load_json(os.path.join(session_dir, "version_verify.json"))
    if not hal_status:
        hal_status = _load_json(os.path.join(session_dir, "hal_status.json"))
    if not script_pool:
        script_pool = _load_json(os.path.join(session_dir, "script_results.json"))

    normalized = []
    for mod in _collect_modules_from_session(session_dir, modules):
        if not isinstance(mod, dict):
            continue
        mid = str(mod.get("module_id") or "unknown")
        mod_dir = os.path.join(detect_dir, "modules", mid)
        os.makedirs(mod_dir, exist_ok=True)
        with open(os.path.join(mod_dir, "module_result.json"), "w", encoding="utf-8") as f:
            json.dump(mod, f, ensure_ascii=False, indent=2)
        write_module_html(mod, os.path.join(mod_dir, "module_result.html"))
        normalized.append(mod)

    result_data = build_bmc_compatible_result_data(
        version_verify=version_verify,
        hal_status=hal_status,
        script_pool=script_pool,
        modules=normalized,
        package_path=pkg,
        progress=progress,
        device_info=di,
    )
    soc_version = soc_version or result_data.get("soc") or ""
    mcu_version = mcu_version or result_data.get("mcu") or ""

    with open(os.path.join(detect_dir, "result_data.json"), "w", encoding="utf-8") as f:
        json.dump(result_data, f, ensure_ascii=False, indent=2)

    # 思维导图 + 测试内容/结果落盘（每次测试 session）
    mindmap_html = ""
    try:
        from t2_session_artifacts import mindmap_html_section, write_session_artifacts

        arts = write_session_artifacts(
            session_dir,
            normalized,
            script_pool=script_pool,
            hal_status=hal_status,
            version_verify=version_verify,
        )
        mindmap_html_root = mindmap_html_section(
            arts["mermaid"], arts["summary"], link_prefix="detect/"
        )
        mindmap_html_detect = mindmap_html_section(
            arts["mermaid"], arts["summary"], link_prefix=""
        )
        print(f"[INFO] 思维导图: {arts.get('test_mindmap')}")
        print(f"[INFO] 测试摘要: {arts.get('test_summary')}")
    except Exception as e:
        print(f"[WARN] 生成思维导图/摘要失败: {e}")
        mindmap_html_root = ""
        mindmap_html_detect = ""

    now = datetime.now()
    common_kwargs = dict(
        pc_id=pc_id,
        pc_name=pc_name,
        soc_version=soc_version,
        mcu_version=mcu_version,
        parent_dir_name=parent_dir_name,
        generated_at=now,
    )
    # detect/ 内报告：链接 modules/...
    html_in_detect = render_bmc_style_report(
        result_data,
        link_prefix="",
        mindmap_section_html=mindmap_html_detect,
        **common_kwargs,
    )
    # 会话根报告：链接 detect/modules/...
    html_at_root = render_bmc_style_report(
        result_data,
        link_prefix="detect/",
        mindmap_section_html=mindmap_html_root,
        **common_kwargs,
    )
    detect_html = os.path.join(detect_dir, "detect_result.html")
    root_html = os.path.join(session_dir, "detect_result.html")
    with open(detect_html, "w", encoding="utf-8") as f:
        f.write(html_in_detect)
    with open(root_html, "w", encoding="utf-8") as f:
        f.write(html_at_root)

    index = {
        "generated_at": now.strftime("%Y-%m-%d %H:%M:%S"),
        "pc_id": pc_id,
        "pc_name": pc_name,
        "package_path": _ota_path_display(pkg),
        "soc_version": soc_version,
        "mcu_version": mcu_version,
        "module_count": len(normalized),
        "modules": [m.get("module_id") for m in normalized],
        "detect_result_html": detect_html,
        "session_detect_result_html": root_html,
        "test_mindmap": os.path.join(session_dir, "test_mindmap.md"),
        "test_summary": os.path.join(session_dir, "test_summary.json"),
    }
    with open(os.path.join(detect_dir, "result_index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False, indent=2)
    print(f"[INFO] 已生成报告: {root_html}")
    print(f"[INFO] 同步副本: {detect_html}")
    return detect_dir


def build_session_report(
    session_dir: str,
    *,
    scripts_dir: str = "",
    package_path: str = "",
    pc_id: str = "",
    pc_name: str = "",
    soc_version: str = "",
    mcu_version: str = "",
) -> str:
    """供 GUI/CLI：仅凭会话目录产物固定生成 detect_result.html。"""
    return build_detect_dir(
        session_dir,
        [],
        scripts_dir=scripts_dir,
        package_path=package_path,
        pc_id=pc_id,
        pc_name=pc_name,
        soc_version=soc_version,
        mcu_version=mcu_version,
    )


def render_hal_detect_report(modules, **kwargs):
    return render_bmc_style_report(
        build_bmc_compatible_result_data(modules=modules, **kwargs),
        **{k: kwargs[k] for k in ("pc_id", "pc_name", "soc_version", "mcu_version", "parent_dir_name") if k in kwargs},
    )


def main(argv: list[str] | None = None) -> int:
    import argparse

    ap = argparse.ArgumentParser(description="从会话目录生成 BMC 风格 detect_result.html")
    ap.add_argument("--session-dir", required=True, help="日志会话目录（含 hal_status.json 等）")
    ap.add_argument("--scripts-dir", default="", help="scripts 根目录（用于读 PC 配置）")
    ap.add_argument("--package-path", default="", help="OTA 包路径；空则写「非ota升级」")
    ap.add_argument("--pc-id", default="")
    ap.add_argument("--pc-name", default="")
    ap.add_argument("--soc-version", default="")
    ap.add_argument("--mcu-version", default="")
    args = ap.parse_args(argv)

    scripts_dir = args.scripts_dir or os.path.dirname(os.path.abspath(__file__))
    build_session_report(
        os.path.abspath(args.session_dir),
        scripts_dir=scripts_dir,
        package_path=args.package_path,
        pc_id=args.pc_id,
        pc_name=args.pc_name,
        soc_version=args.soc_version,
        mcu_version=args.mcu_version,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
