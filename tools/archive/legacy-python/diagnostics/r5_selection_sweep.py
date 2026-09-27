#!/usr/bin/env python3
"""R5 目标选择扫描：C# 选择器 vs 上游 `Filter`（离线，无设备）。

用法：
    python tools/diagnostics/r5_selection_sweep.py [--states 200] [--seed 7]

做的事：
  1. 用**确定性随机**造一批地图状态（格数、敌人 scale/genre/weight/cost 都随机，
     含 `cost=9999` 的不可达格），与 `preserve` 组合；
  2. 过滤器串取自**库里的真实用法**（`clear_filter_enemy` 的 5 个不同串）加上两个优先级预设
     （`S3_enemy_first` / `S1_enemy_first`，与上游 `args.json` 的取值一致）；
  3. 全部用例进一份夹具喂给 `Alas.Server r5-select`；
  4. 上游侧复用 `verify_r5_selection.upstream_filter_selection`（真调用 `module.base.filter.Filter`）
     算同一份期望，逐例比对；
  5. 报告写 `docs/archive/reports/r5-selection-sweep.md`（脚本重建），有差异即非零退出。

口径：只覆盖 `clear_filter_enemy` 的**过滤器 + 排序 + preserve** 语义（`Filter` DSL）；
`clear_enemy` / `clear_boss` 等其它选择分支由夹具用例覆盖，不在本扫描范围内。
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
sys.path.insert(0, str(ROOT / "tools" / "diagnostics"))
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-selection-sweep.md"
WORK = ROOT / ".runtime" / "r5-probe"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"

from verify_r5_selection import upstream_filter_selection  # noqa: E402  (上游 Filter 对拍逻辑)

# 库里的真实过滤器串（`clear_filter_enemy` 的 positional 实参统计得到）
FILTERS = [
    "1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C",
    "1T > 1L > 1E > 1M > 2T > 2L > 2E > 2M > 3T > 3L > 3E > 3M",
    "1L > 1M > 1E > 2L > 3L > 2M > 2E > 1C > 2C > 3M > 3E > 3C",
    "1L > 1M > 2L > 2M > 3L > 3M > 1E > 2E > 3E > 1C > 2C > 3C",
    "1L > 1M > 2L > 2M > 3L > 2E > 3E > 2C > 3C > 3M",
]
GENRES = ["Main", "Light", "Carrier", "Treasure", "Enemy"]   # 上游真实取值（Heavy/Torpedo 不存在）


def build_cases(states: int, seed: int) -> list[dict]:
    rng = random.Random(seed)
    cases = []
    for index in range(states):
        width = rng.randint(3, 6)
        height = rng.randint(2, 4)
        grids = []
        for y in range(height):
            for x in range(width):
                is_enemy = rng.random() < 0.45
                cost = 9999 if rng.random() < 0.15 else rng.randint(1, 25)
                grids.append({
                    "location": f"{chr(65 + x)}{y + 1}",
                    "is_enemy": is_enemy,
                    "is_boss": is_enemy and rng.random() < 0.15,
                    "enemy_scale": rng.choice([1, 2, 3]),
                    "enemy_genre": rng.choice(GENRES),
                    "weight": rng.choice([0, 10, 20, 30, 40, 50, 60, 70, 80, 90]),
                    "cost": cost,
                })
        for filter_index, filter_text in enumerate(FILTERS):
            for preserve in (0, 1, 2):
                if rng.random() < 0.4:            # 抽样，避免用例爆炸
                    continue
                cases.append({
                    # **名字必须唯一**：同名会让结果字典互相覆盖，对拍配对错乱（实测踩过）
                    "name": f"state{index}-f{filter_index}-preserve{preserve}",
                    "kind": "clear_filter_enemy",
                    "filter": filter_text,
                    "preserve": preserve,
                    "grids": grids,
                })
        for priority in ("S3_enemy_first", "S1_enemy_first"):
            cases.append({
                "name": f"state{index}-{priority}",
                "kind": "clear_filter_enemy",
                "enemy_priority": priority,
                "preserve": 0,
                "grids": grids,
            })
    return cases


def main() -> int:
    parser = argparse.ArgumentParser(description="R5 目标选择扫描")
    parser.add_argument("--states", type=int, default=200)
    parser.add_argument("--seed", type=int, default=7)
    options = parser.parse_args()
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")

    cases = build_cases(options.states, options.seed)
    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / "selection-sweep-fixture.json"
    fixture.write_text(json.dumps({"cases": cases}, ensure_ascii=False), encoding="utf-8", newline="\n")

    completed = subprocess.run([str(SERVER), "r5-select", "--fixture", str(fixture)],
                               cwd=ROOT, capture_output=True, text=True, timeout=1800,
                               encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout[-2000:], completed.stderr[-2000:])
        raise SystemExit(f"r5-select 退出码 {completed.returncode}")
    results = {item["name"]: item for item in json.loads(completed.stdout)["cases"]}

    mismatches: list[tuple[str, str, str]] = []
    for case in cases:
        result = results.get(case["name"])
        if result is None:
            mismatches.append((case["name"], "命令没返回这个用例", ""))
            continue
        expected = upstream_filter_selection(case)
        if result["selected"] != expected:
            detail = (f"filter={case.get('filter') or case.get('enemy_priority')} "
                      f"preserve={case['preserve']}")
            mismatches.append((case["name"], detail, f"C# {result['selected']!r} vs 上游 {expected!r}"))

    lines = ["# R5 目标选择扫描（C# 选择器 vs 上游 `Filter`）", "",
             "> 本报告由 `tools/diagnostics/r5_selection_sweep.py` 重建，不手写。",
             "> 覆盖范围：`clear_filter_enemy` 的**过滤器 + 排序 + preserve** 语义（上游 `Filter` DSL）；",
             "> 过滤器串取自库里的真实用法（5 个不同串）加两个优先级预设；状态是**确定性随机**生成的。", "",
             f"- 状态数：**{options.states}**（seed={options.seed}）",
             f"- 用例数：**{len(cases)}**",
             f"- 不一致：**{len(mismatches)}**", ""]
    if mismatches:
        lines += ["## 不一致（前 20 条）", "", "| 用例 | 参数 | 差异 |", "| --- | --- | --- |"]
        lines += [f"| {name} | `{detail}` | {diff} |" for name, detail, diff in mismatches[:20]]
        lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：{len(cases)} 个用例 / 不一致 {len(mismatches)}")
    for item in mismatches[:5]:
        print("  -", item)
    return 1 if mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
