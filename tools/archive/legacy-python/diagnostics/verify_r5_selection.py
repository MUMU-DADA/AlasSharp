#!/usr/bin/env python3
"""R5 目标选择对拍：C# 移植的选择器 vs 上游算法（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_selection.py

对拍方式：
  1. 用夹具 tools/diagnostics/r5-selection-fixture.json 跑产品命令
     `Alas.Server r5-select --fixture <夹具>`，逐例核对夹具里声明的 expect；
  2. 对 clear_filter_enemy 用例，**真调用上游** `module.base.filter.Filter`（离线可导入），
     按上游 `clear_filter_enemy` 的同一顺序（is_enemy+is_accessible → sort('weight','cost') →
     文本过滤 → preserve 截断）算出应选中的格子，与 C# 结果比对。

只做"选哪个格子"的决策对拍：不连设备、不执行游戏动作。
"""
from __future__ import annotations

import json
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tools" / "diagnostics" / "r5-selection-fixture.json"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def upstream_root() -> pathlib.Path:
    configured = os.environ.get("ALAS_REPO")
    path = pathlib.Path(configured) if configured else ROOT / ".runtime" / "engine"
    if not (path / "module" / "base" / "filter.py").is_file():
        raise SystemExit(f"找不到上游 ALAS 检出：{path}（用 ALAS_REPO 指定）")
    return path


class UpstreamGrid:
    """上游 Filter 只按属性比较：构造等价的替身即可，不需要真实 Map/GridInfo。"""

    def __init__(self, item: dict) -> None:
        self.item = item
        self.str = grid_str(item)
        self.weight = item.get("weight", 0)
        self.cost = item.get("cost", 0)

    @property
    def is_enemy(self) -> bool:
        return bool(self.item.get("is_enemy"))

    @property
    def is_boss(self) -> bool:
        return bool(self.item.get("is_boss"))

    @property
    def is_accessible(self) -> bool:
        return self.item.get("cost", 0) < 9999

    def __repr__(self) -> str:
        return self.item.get("location", "?")


def grid_str(item: dict) -> str:
    """上游 `GridInfo.encode()`（`module/map_detection/grid_info.py`）的**前几支**。

    分支顺序必须照抄上游，否则会得出错的期望（实测踩过）：
        `++`（is_land）→ `BO`（is_boss）→ 塞壬分支 → **敌人**分支 `{scale}{genre[0]|E}` → …
    原来的替身只写了敌人分支，`is_boss` 的格子被当成 `1E` 之类，于是扫描报了 421 处
    "不一致"——**其实是替身不忠实**，不是引擎选错。
    """
    if item.get("is_land"):
        return "++"
    if item.get("is_boss"):
        return "BO"
    if not item.get("is_enemy"):
        return ""
    genre = item.get("enemy_genre") or ""
    return f"{item.get('enemy_scale', 0)}{genre[0].upper() if genre else 'E'}"


def upstream_filter_selection(case: dict) -> str | None:
    """按上游 clear_filter_enemy 的顺序算出应选中的格子位置；None 表示无目标。"""
    engine = upstream_root()
    if str(engine) not in sys.path:
        sys.path.insert(0, str(engine))
    from module.base.filter import Filter  # noqa: PLC0415  (上游实现，离线可导入)

    enemy_filter = Filter(regex=re.compile("^(.*?)$"), attr=("str",))
    text = case.get("filter") or ""
    preserve = int(case.get("preserve") or 0)
    priority = case.get("enemy_priority")
    if priority == "S3_enemy_first":
        text = "3L > 3M > 3E > 3C > 2L > 2M > 2E > 2C > 1L > 1M > 1E > 1C"
        preserve = 0
    elif priority == "S1_enemy_first":
        text = "1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C"

    grids = [UpstreamGrid(item) for item in case["grids"]]
    grids = [g for g in grids if g.is_enemy and g.is_accessible]
    if not grids:
        return None
    grids = sorted(grids, key=lambda g: (g.weight, g.cost))
    enemy_filter.load(text)
    filtered = enemy_filter.apply(grids)
    if preserve:
        filtered = filtered[preserve:]
    return filtered[0].item["location"] if filtered else None


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    completed = subprocess.run([str(SERVER), "r5-select", "--fixture", str(FIXTURE)],
                               cwd=ROOT, capture_output=True, text=True, timeout=120,
                               encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout, completed.stderr)
        raise SystemExit(f"r5-select 退出码 {completed.returncode}")
    payload = json.loads(completed.stdout)
    results = {item["name"]: item for item in payload["cases"]}

    problems: list[str] = []
    checked = cross_checked = 0
    for case in fixture["cases"]:
        name = case["name"]
        result = results.get(name)
        checked += 1
        if result is None:
            problems.append(f"{name}: 命令没有返回这个用例")
            continue
        expect = case.get("expect") or {}
        if "selected" in expect and result["selected"] != expect["selected"]:
            problems.append(f"{name}: 选中 {result['selected']!r}，期望 {expect['selected']!r}"
                            f"（分支 {result['branch']}）")
        if "selected_str" in expect and result["selected_str"] != expect["selected_str"]:
            problems.append(f"{name}: 选中编码 {result['selected_str']!r}，期望 {expect['selected_str']!r}")
        if "branch_contains" in expect and expect["branch_contains"] not in (result["branch"] or ""):
            problems.append(f"{name}: 分支 {result['branch']!r} 不含 {expect['branch_contains']!r}")
        if "unsupported_contains" in expect:
            text = result.get("unsupported") or ""
            if expect["unsupported_contains"] not in text:
                problems.append(f"{name}: 未移植说明 {text!r} 不含 {expect['unsupported_contains']!r}")

        # 对拍：clear_filter_enemy 且走上游分支时，与上游 Filter 的结果逐例比对
        if case.get("kind") == "clear_filter_enemy" and not case.get("has_movable_normal_enemy"):
            expected = upstream_filter_selection(case)
            cross_checked += 1
            if result["selected"] != expected:
                problems.append(f"{name}: C# 选中 {result['selected']!r}，上游 Filter 算出 {expected!r}")

    print(f"[r5-select] 夹具用例 {checked} 个，其中与上游 Filter 直接对拍 {cross_checked} 个")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 目标选择与上游算法一致（含 Filter 直接对拍）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
