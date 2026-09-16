# -*- coding: utf-8 -*-
from pathlib import Path
import re
import json

root = Path(r"c:\Users\yuhao\Desktop\T2自测工具\docs\hal_modules")
mods = [
    "audioctrl", "light", "ampservice", "localradio", "metazone", "input", "drinfo",
    "gnssdr", "anc", "someip", "installerhal", "diag", "devmanager", "cameractrl",
    "securitychip", "most", "rse", "earlycarservice", "maintainhal", "vehicle",
    "vehicleconfig",
]
for mid in mods:
    p = root / mid / "README.md"
    if not p.exists():
        print("skip", mid)
        continue
    t = p.read_text(encoding="utf-8")
    new = f"- `scripts/hal_selfcheck/{mid}/`：**已有 L1**（`run.sh` / `manifest.json`；本机 ipc+binder+lshal，无外设）"
    if mid == "vehicleconfig":
        new = f"- `scripts/hal_selfcheck/{mid}/`：**已有 L1+L2**（含写参还原样板）"
    pat = re.compile(rf"- `scripts/hal_selfcheck/{re.escape(mid)}/`：[^\n]+")
    if pat.search(t):
        t2 = pat.sub(new, t)
    elif "脚本池现状" in t:
        t2 = re.sub(r"(## 3\. 脚本池现状\n\n)(-[^\n]+)", r"\1" + new, t, count=1)
    else:
        t2 = t
        print("no patch", mid)
        continue
    p.write_text(t2, encoding="utf-8")
    print("ok", mid)

# mostslave note under most README if needed
r = Path(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\hal_selfcheck")
dirs = sorted([p.name for p in r.iterdir() if p.is_dir() and (p / "run.sh").exists()])
print("run.sh dirs", len(dirs), dirs)
pool = json.load(open(r"c:\Users\yuhao\Desktop\T2自测工具\scripts\t2_script_pool.json", encoding="utf-8"))
print("pool", len(pool["scripts"]))
