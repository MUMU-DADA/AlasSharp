#!/usr/bin/env python3
"""R5 选择分支扫描：C# `SelectGrids` vs 上游 `Map.select_grids`（离线，无设备）。

用法：
    python tools/diagnostics/r5_selectgrids_sweep.py [--states 120] [--seed 11]

覆盖的是 `select_grids` 的**分支与循环取值**（`clear_enemy` / `clear_boss` / `clear_siren` /
`clear_any_enemy` 等都以它为核心）：
  * `nearby`（上游 `is_nearby = cost < 20`）、`is_accessible = cost < 9999`；
  * `scale`：**tuple = 无序**（合并各档的并集）vs **list = 有序**（命中即停）——上游注释写明了这个区别；
  * `genre`：同样区分无序/有序，且**大小写不敏感**（上游会把首字母大写）；
  * `strongest` / `weakest`（上游按 weight/cost 排序取末/首）；
  * `sort`：多键稳定排序（`weight` / `cost` / `cost_1` / `cost_2`）。

上游侧**真调用** `Map.select_grids`（它是静态方法，离线可调），用带属性的替身格子驱动；
C# 侧走 `Alas.Server r5-select --fixture`，逐例比较选中的格子。

只读：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import argparse
import json
import pathlib
import random
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-selectgrids-sweep.md"
WORK = ROOT / ".runtime" / "r5-probe"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
UPSTREAM = ROOT / ".runtime" / "engine"

GENRES = ["Light", "Main", "Carrier", "Treasure"]
SORTS = [["weight", "cost"], ["cost"], ["weight"], ["cost_1", "cost_2"]]


def build_cases(states: int, seed: int) -> list[dict]:
    rng = random.Random(seed)
    cases = []
    for index in range(states):
        width, height = rng.randint(3, 5), rng.randint(2, 4)
        grids = []
        for y in range(height):
            for x in range(width):
                cost = 9999 if rng.random() < 0.2 else rng.randint(1, 28)
                grids.append({
                    "location": f"{chr(65 + x)}{y + 1}",
                    "is_enemy": rng.random() < 0.5,
                    "is_boss": rng.random() < 0.15,
                    "is_siren": rng.random() < 0.1,
                    "is_mystery": rng.random() < 0.15,
                    "enemy_scale": rng.choice([1, 2, 3]),
                    "enemy_genre": rng.choice(GENRES),
                    "weight": rng.choice([0, 10, 20, 30, 40, 50, 60, 70, 80, 90]),
                    "cost": cost,
                    "cost_1": 9999 if rng.random() < 0.3 else rng.randint(1, 28),
                    "cost_2": 9999 if rng.random() < 0.3 else rng.randint(1, 28),
                })
        combos = [
            {"nearby": True, "is_accessible": True},
            {"nearby": False, "is_accessible": False},
            {"scale": [1], "scale_in_order": False},
            {"scale": [2, 3], "scale_in_order": True},
            {"scale": [2, 3], "scale_in_order": False},
            {"genre": ["Light"], "genre_in_order": False},
            {"genre": ["main", "treasure"], "genre_in_order": True},
            {"strongest": True},
            {"weakest": True},
            {"sort": ["cost"]},
            {"sort": ["weight"]},
            {"sort": ["cost_1", "cost_2"]},
            {"nearby": True, "scale": [1, 2], "strongest": True, "sort": ["weight", "cost"]},
            {"is_accessible": True, "genre": ["Carrier"], "weakest": True},
        ]
        for combo_index, combo in enumerate(combos):
            cases.append({
                "name": f"state{index}-combo{combo_index}",
                "kind": "select_grids",
                "grids": grids,
                "options": combo,
            })
    return cases


class StubGrid:
    """上游 `SelectedGrids.select` 只做属性比对（还要求类型相同），替身给全属性即可。"""

    def __init__(self, item: dict) -> None:
        self.location = item["location"]
        self.is_enemy = bool(item.get("is_enemy"))
        self.is_boss = bool(item.get("is_boss"))
        self.is_siren = bool(item.get("is_siren"))
        self.is_mystery = bool(item.get("is_mystery"))
        self.enemy_scale = int(item.get("enemy_scale", 0))
        self.enemy_genre = item.get("enemy_genre")
        self.weight = int(item.get("weight", 0))
        self.cost = int(item.get("cost", 9999))
        self.cost_1 = int(item.get("cost_1", 9999))
        self.cost_2 = int(item.get("cost_2", 9999))

    @property
    def is_accessible(self) -> bool:
        return self.cost < 9999

    @property
    def is_nearby(self) -> bool:
        return self.cost < 20

    def __str__(self) -> str:
        return self.location

    def __repr__(self) -> str:
        return self.location


def upstream_selection(case: dict) -> str | None:
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))
    from module.map.map import Map  # noqa: PLC0415
    from module.map.map_grids import SelectedGrids  # noqa: PLC0415

    options = case.get("options") or {}
    grids = SelectedGrids([StubGrid(item) for item in case["grids"]])
    scale = options.get("scale") or []
    genre = options.get("genre") or []
    result = Map.select_grids(
        grids,
        nearby=bool(options.get("nearby")),
        is_accessible=options.get("is_accessible", True),
        # tuple = 无序（并集），list = 有序（命中即停）——上游按类型区分，这里照做
        scale=tuple(scale) if not options.get("scale_in_order") else list(scale),
        genre=tuple(genre) if not options.get("genre_in_order") else list(genre),
        strongest=bool(options.get("strongest")),
        weakest=bool(options.get("weakest")),
        sort=tuple(options.get("sort") or ("weight", "cost")),
    )
    return str(result[0]) if result else None


def main() -> int:
    parser = argparse.ArgumentParser(description="R5 选择分支扫描")
    parser.add_argument("--states", type=int, default=120)
    parser.add_argument("--seed", type=int, default=11)
    options = parser.parse_args()
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")

    cases = build_cases(options.states, options.seed)
    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / "selectgrids-sweep-fixture.json"
    fixture.write_text(json.dumps({"cases": cases}, ensure_ascii=False), encoding="utf-8", newline="\n")

    completed = subprocess.run([str(SERVER), "r5-select", "--fixture", str(fixture)],
                               cwd=ROOT, capture_output=True, text=True, timeout=1800,
                               encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout[-1500:], completed.stderr[-1500:])
        raise SystemExit(f"r5-select 退出码 {completed.returncode}")
    results = {item["name"]: item for item in json.loads(completed.stdout)["cases"]}

    mismatches: list[tuple[str, str, str]] = []
    for case in cases:
        result = results.get(case["name"])
        if result is None:
            mismatches.append((case["name"], json.dumps(case["options"], ensure_ascii=False),
                               "命令没返回这个用例"))
            continue
        expected = upstream_selection(case)
        if result["selected"] != expected:
            mismatches.append((case["name"], json.dumps(case["options"], ensure_ascii=False),
                               f"C# {result['selected']!r} vs 上游 {expected!r}"))

    lines = ["# R5 选择分支扫描（C# `SelectGrids` vs 上游 `Map.select_grids`）", "",
             "> 本报告由 `tools/diagnostics/r5_selectgrids_sweep.py` 重建，不手写。",
             "> 覆盖：`nearby` / `is_accessible` / `scale`（无序 vs 有序）/ `genre`（含大小写不敏感）/",
             "> `strongest` / `weakest` / `sort`（多键稳定排序）；上游侧**真调用** `Map.select_grids`。", "",
             f"- 状态数：**{options.states}**（seed={options.seed}）",
             f"- 用例数：**{len(cases)}**",
             f"- 不一致：**{len(mismatches)}**", ""]
    if mismatches:
        lines += ["## 不一致（前 20 条）", "", "| 用例 | 选项 | 差异 |", "| --- | --- | --- |"]
        lines += [f"| {name} | `{opts}` | {diff} |" for name, opts, diff in mismatches[:20]]
        lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：{len(cases)} 个用例 / 不一致 {len(mismatches)}")
    for item in mismatches[:5]:
        print("  -", item)
    return 1 if mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
