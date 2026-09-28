#!/usr/bin/env python3
"""R5 对拍扫描的**新鲜度棘轮**：重跑扫描，把不一致数与已声明上限比。

背景（本轮发现的问题）：`docs/archive/reports/r5-selection-sweep.md` 曾以"零不一致"提交，
但选择器在那之后又改过（`b7eccc3` 补 movable 分支），而**这些扫描不在 `verify_all.py` 里**，
没人重跑 —— 报告过期，实际重跑是 **421/2166 不一致**。

本检查把这种漂移变成看得见、且**只许下降**的数：
    * 重跑 `r5_selection_sweep.py`（它自己会重建报告）；
    * 读报告里的"不一致"条数；
    * 与 <MAX_MISMATCHES> 比较，涨了就失败。

只读：不连设备、不改仓库数据（只重建 docs/archive/reports 下的扫描报告）。
"""
from __future__ import annotations

import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
PYTHON = ROOT / ".runtime" / "venv314" / "Scripts" / "python.exe"
SWEEP = ROOT / "tools" / "diagnostics" / "r5_selection_sweep.py"
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-selection-sweep.md"

# 已声明上限（只许下降）：**0**。曾经因为替身不忠实（漏了 boss/land 分支）报过 421 处假红，
MAX_MISMATCHES = 0


def main() -> int:
    if not PYTHON.is_file():
        raise SystemExit(f"缺少 {PYTHON.relative_to(ROOT)}")
    completed = subprocess.run([str(PYTHON), str(SWEEP)], cwd=str(ROOT),
                               capture_output=True, text=True, encoding="utf-8", errors="replace")
    text = REPORT.read_text(encoding="utf-8")
    cases = re.search(r"用例数：\*\*(\d+)\*\*", text)
    mismatch = re.search(r"不一致：\*\*(\d+)\*\*", text)
    if not cases or not mismatch:
        print(completed.stdout[-2000:], completed.stderr[-2000:])
        print("FAIL: 报告里没有解析到用例数/不一致数")
        return 1
    total, bad = int(cases.group(1)), int(mismatch.group(1))
    print(f"[r5-selection-sweep] 重跑：{total} 例，不一致 {bad}（已声明上限 {MAX_MISMATCHES}）")
    if bad > MAX_MISMATCHES:
        print(f"FAIL: 不一致从 {MAX_MISMATCHES} 涨到 {bad}——要么是回归，要么是上限该更新（先查清再改这个数）")
        return 1
    if bad == 0:
        print("PASS: 目标选择扫描零不一致")
        return 0
    print(f"PASS（带缺口）：{bad} 处不一致已在 <MAX_MISMATCHES> 里声明；"
          f"这是**已知缺口**，不是本次改动引入的（详见报告）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
