# -*- coding: utf-8 -*-
"""Regenerate BMCSJB soft-skip run.sh from template + cases/; restore vehicleconfig carefully."""
from __future__ import annotations

import json
import re
import shutil
from pathlib import Path

BMCSJB = Path(r"E:\BMCSJB\T2TestScript")
HAL_SC = Path(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\hal_selfcheck")

# import template helpers
import sys
sys.path.insert(0, str(HAL_SC))
from _patch_soft_skip import META, SOFT_RUN_TEMPLATE, GIT, BRANCH, indent_l2  # noqa: E402


def l2_block_from_cases(module_dir: Path) -> str:
    cases = sorted((module_dir / "cases").glob("*.sh")) if (module_dir / "cases").is_dir() else []
    if not cases:
        return "# (no L2)"
    lines = []
    for c in cases:
        cid = c.stem
        # try name from first comment line
        name = cid
        try:
            first = c.read_text(encoding="utf-8", errors="ignore").splitlines()[:8]
            for ln in first:
                if "—" in ln or "-" in ln and cid in ln:
                    pass
        except Exception:
            pass
        env = f"HAL_ENABLE_{cid.upper()}"
        lines.append(
            f'run_l2_if_enabled "${{{env}:-false}}" \\\n'
            f'  "$SCRIPT_DIR/cases/{cid}.sh" \\\n'
            f'  "{cid}" "{name}" "false"'
        )
    return "\n\n".join(lines)


def write_soft_run(mid: str) -> None:
    d = BMCSJB / mid
    mf = {}
    mp = d / "manifest.json"
    if mp.exists():
        mf = json.loads(mp.read_text(encoding="utf-8"))
    meta = META.get(mid, {"soft": True, "deps": ""})
    # prefer existing PROCESS etc from hal_selfcheck good copy
    src = HAL_SC / mid / "run.sh"
    binary = mf.get("hal_binary", "")
    lshal = mf.get("lshal_needle", "") or ""
    process = tomb = ""
    mode, bin_opt = "hidl", "false"
    probe = src if src.exists() else (d / "run.sh")
    if probe.exists():
        for ln in probe.read_text(encoding="utf-8", errors="replace").splitlines():
            if ln.startswith("PROCESS_PAT="):
                process = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("TOMBSTONE_KW="):
                tomb = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("MODE="):
                mode = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("BINARY_OPTIONAL="):
                bin_opt = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("LSHAL_NEEDLE=") and not lshal:
                lshal = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("HAL_BINARY=") and not binary:
                binary = ln.split("=", 1)[1].strip().strip('"')
            elif ln.startswith("MODULE_NAME="):
                pass
    if not process:
        process = f"{mid}@1.0-service"
    if not tomb:
        tomb = mid
    l2 = l2_block_from_cases(d)
    # if no cases, keep "# (no L2)"
    text = SOFT_RUN_TEMPLATE
    text = text.replace("@@MID@@", mid)
    text = text.replace("@@NAME@@", mf.get("module_name", mid))
    text = text.replace("@@BINARY@@", binary)
    text = text.replace("@@PROCESS@@", process)
    text = text.replace("@@LSHAL@@", lshal)
    text = text.replace("@@TOMB@@", tomb)
    text = text.replace("@@BINOPT@@", bin_opt)
    text = text.replace("@@MODE@@", mode)
    text = text.replace("@@SOFT@@", "1" if meta["soft"] else "0")
    text = text.replace("@@DEPS@@", meta["deps"])
    text = text.replace("@@GIT@@", GIT)
    text = text.replace("@@BRANCH@@", BRANCH)
    text = text.replace("@@L2_INDENT@@", indent_l2(l2).rstrip("\n"))
    text = text.replace("@@L2_BLOCK@@", l2 if l2.strip() else "# (no L2)")
    # sanity: braces
    if text.count("{") != text.count("}"):
        raise RuntimeError(f"{mid} brace mismatch {text.count('{')}/{text.count('}')}")
    if "run_l2_if_enabled() {" in text.split("memleak_probe")[-1]:
        raise RuntimeError(f"{mid} orphan function in tail")
    (d / "run.sh").write_text(text.replace("\r\n", "\n"), encoding="utf-8", newline="\n")
    # sync to hal_selfcheck too
    if (HAL_SC / mid).is_dir() and mid != "vehicleconfig":
        (HAL_SC / mid / "run.sh").write_text(text.replace("\r\n", "\n"), encoding="utf-8", newline="\n")
    print("OK soft", mid, "lines", text.count("\n") + 1)


def restore_vehicleconfig() -> None:
    """vehicleconfig uses dedicated run.sh (not soft template). Prefer package backup / reconstruct from log-proven version."""
    vc = BMCSJB / "vehicleconfig"
    # If current is soft-template corrupted, restore from scripts/hal_selfcheck if still custom,
    # else keep BMCSJB copy if it still has vehicleconfig-specific content.
    cur = (vc / "run.sh").read_text(encoding="utf-8", errors="replace")
    if "VC_TEST_CONFIG" in cur and "vehicleconfig" in cur and cur.count("{") == cur.count("}"):
        print("vehicleconfig already OK")
        return
    # search candidates
    cands = [
        Path(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\hal_selfcheck\vehicleconfig\run.sh"),
        Path(r"c:\Users\yuhao\Desktop\T2自测工具\script_pool_packages\vehicleconfig_hal\run.sh"),
    ]
    for c in cands:
        if c.exists():
            t = c.read_text(encoding="utf-8", errors="replace")
            if "VC_TEST_CONFIG" in t and t.count("{") == t.count("}"):
                shutil.copy2(c, vc / "run.sh")
                # ensure LF
                raw = (vc / "run.sh").read_bytes().replace(b"\r\n", b"\n")
                (vc / "run.sh").write_bytes(raw)
                print("vehicleconfig restored from", c)
                return
    raise RuntimeError("no good vehicleconfig run.sh candidate")


def main() -> None:
    restore_vehicleconfig()
    for mid in META:
        if mid == "vehicleconfig":
            continue
        if not (BMCSJB / mid / "manifest.json").exists():
            continue
        write_soft_run(mid)
    # validate all
    bad = []
    for p in BMCSJB.glob("*/run.sh"):
        t = p.read_text(encoding="utf-8")
        if t.count("{") != t.count("}"):
            bad.append((p.parent.name, "brace"))
        if re.search(r"(?m)^run_l2_if_enabled\(\) \{\s*$", t) and "memleak_probe" in t:
            # allow the real function def near top; flag only if AFTER memleak
            tail = t.split("memleak_probe", 1)[-1]
            if "run_l2_if_enabled() {" in tail:
                bad.append((p.parent.name, "orphan"))
        if "finish_and_exit" not in t and "MODULE=" not in t:
            # vehicleconfig uses different exit path
            if p.parent.name != "vehicleconfig":
                bad.append((p.parent.name, "no_finish"))
    print("bad", bad)


if __name__ == "__main__":
    main()
