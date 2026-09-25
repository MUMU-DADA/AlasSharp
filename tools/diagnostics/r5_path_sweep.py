#!/usr/bin/env python3
"""R5 寻路全库对拍：对**所有带声明地图的关卡**逐格比较 C# 移植与上游实现（离线，无设备）。

用法：
    python tools/diagnostics/r5_path_sweep.py [--limit N]

做的事：
  1. 从 `data/campaign/**/*.json` 取每个带 `map.map_data` 的关卡，起点取第一个**非陆地**格
     （优先 `SP` 出生点）；
  2. 生成一份多用例夹具喂给 `Alas.Server r5-path`（C# 侧：成本场 + 连接）；
  3. 上游侧对同一张图跑 `CampaignMap.load_map_data()` + `grid_connection_initial(wall=True)` +
     `find_path_initial(start, has_ambush, has_enemy)`，逐格比较 **cost** 与 **connection**；
  4. 报告写 `docs/archive/reports/r5-path-sweep.md`（脚本重建），有任何不一致就非零退出。

口径：比较的是**同一份声明地图、同一算法**的成本场与连接；识别结果（敌人/机关）不参与，
所以这证明不了"真机识别下的寻路"，只证明**算法移植一致**。
只读：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import argparse
import json
import pathlib
import statistics
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-path-sweep.md"
WORK = ROOT / ".runtime" / "r5-probe"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
UPSTREAM = ROOT / ".runtime" / "engine"


def collect(limit: int) -> list[dict]:
    cases = []
    for path in sorted((ROOT / "data" / "campaign").rglob("*.json")):
        payload = json.loads(path.read_text(encoding="utf-8"))
        mapping = payload.get("map") or {}
        data = mapping.get("map_data")
        if not isinstance(data, str) or not data.strip():
            continue
        rows = [row.strip() for row in data.strip().splitlines() if row.strip()]
        start = None
        for y, row in enumerate(rows):
            for x, token in enumerate(row.split()):
                node = f"{chr(ord('A') + x)}{y + 1}"
                if token == "SP":
                    start = node
                    break
                if start is None and token != "++":
                    start = node
            if start is not None and any(token == "SP" for token in row.split()):
                break
        if start is None:
            continue
        # 路线对拍用的目标：均匀取若干个格（含最后一个），控制用例体积又不漏掉远端。
        nodes = [f"{chr(ord('A') + x)}{y + 1}" for y, row in enumerate(rows) for x, _ in enumerate(row.split())]
        step = max(len(nodes) // 8, 1)
        destinations = nodes[::step][:8] or nodes[:1]
        # 三种配置都跑：上游 `find_path_initial(location, has_ambush, has_enemy)` 的这两个开关
        # 会改变代价与扩散规则（伏击格代价 10 / `not has_enemy` 时敌人格也继续扩散），
        # 所以它们各自是一块真实的算法面。
        for has_ambush, has_enemy, label in ((False, True, "默认"), (True, True, "有伏击"), (False, False, "不考虑敌人")):
            cases.append({
                "name": f"{str(path.relative_to(ROOT / 'data' / 'campaign'))[:-5]}#{label}",
                "level": str(path.relative_to(ROOT / "data" / "campaign"))[:-5],
                "config": label,
                "rows": rows,
                "start": start,
                "has_ambush": has_ambush,
                "has_enemy": has_enemy,
                "destinations": destinations,
            })
        if limit and len(cases) >= limit:
            break
    return cases


def csharp_field(cases: list[dict]) -> dict[str, dict]:
    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / "path-sweep-fixture.json"
    fixture.write_text(json.dumps({"cases": cases}, ensure_ascii=False), encoding="utf-8", newline="\n")
    completed = subprocess.run([str(SERVER), "r5-path", "--fixture", str(fixture), "--json"],
                               cwd=ROOT, capture_output=True, text=True, timeout=1800,
                               encoding="utf-8", errors="replace")
    text = completed.stdout + completed.stderr
    start = text.find("{")
    if start < 0:
        raise SystemExit(f"r5-path 没有输出 JSON：{text.strip().splitlines()[-3:]}")
    payload = json.loads(text[start:])
    return {case["name"]: case for case in payload["cases"]}


def upstream_field(case: dict) -> tuple[dict, dict, dict]:
    """上游侧：成本场 + 连接 + **路线**（对 `destinations` 逐个调上游自己的 `_find_path`）。"""
    sys.path.insert(0, str(UPSTREAM))
    from module.base.utils import location2node, node2location  # noqa: PLC0415
    from module.map.map_base import CampaignMap  # noqa: PLC0415

    campaign_map = CampaignMap("sweep")
    campaign_map.shape = location2node((max(len(row.split()) for row in case["rows"]) - 1,
                                        len(case["rows"]) - 1))
    campaign_map.map_data = "\n".join(case["rows"])
    campaign_map.load_map_data()
    campaign_map.grid_connection_initial(wall=True)
    campaign_map.find_path_initial(tuple(node2location(case["start"])),
                                   has_ambush=case["has_ambush"], has_enemy=case["has_enemy"])
    costs, connections, ambush = {}, {}, {}
    for grid in campaign_map:
        node = location2node(grid.location)
        costs[node] = grid.cost
        connections[node] = location2node(grid.connection) if grid.connection is not None else None
        ambush[node] = bool(grid.may_ambush)
    routes: dict[str, list[str] | None] = {}
    for destination in case.get("destinations") or []:
        route = campaign_map._find_path(tuple(node2location(destination)))
        routes[destination] = None if route is None else [location2node(item) for item in route]
    return costs, connections, routes, ambush


def neighbours_of(node: str) -> set[str]:
    """四邻接（与上游 `grid_connection_initial` 同一套）：用来校验路线每一步都相邻。"""
    x, y = ord(node[0]) - 65, int(node[1:]) - 1
    return {f"{chr(65 + nx)}{ny + 1}" for nx, ny in ((x, y - 1), (x, y + 1), (x - 1, y), (x + 1, y))
            if nx >= 0 and ny >= 0}


def route_cost(connections: dict, costs: dict, target: str, start: str) -> int | None:
    """沿 connection 从 target 回溯到 start，累计每步代价（与成本场同一算法）。"""
    steps = 0
    current = target
    seen = set()
    while current != start:
        if current in seen:
            return None                        # 成环：不正常
        seen.add(current)
        nxt = connections.get(current)
        if nxt is None:
            return None
        # 每步代价在成本场里体现为差值（伏击 10 / 普通 1），用相邻两格成本差即可
        steps += max(costs.get(current, 0) - costs.get(nxt, 0), 0) or 1
        current = nxt
        if len(seen) > 10000:
            return None
    return steps


def main() -> int:
    parser = argparse.ArgumentParser(description="R5 寻路全库对拍")
    parser.add_argument("--limit", type=int, default=0, help="只跑前 N 关（0 = 全部）")
    options = parser.parse_args()
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")

    cases = collect(options.limit)
    if not cases:
        raise SystemExit("没有找到带声明地图的关卡导出")
    began = time.perf_counter()
    csharp = csharp_field(cases)
    csharp_seconds = time.perf_counter() - began

    compared = 0
    cost_diffs: list[tuple[str, str, object, object]] = []      # C# 比上游**更差**（真问题）
    artifacts: list[tuple[str, str, object, object]] = []        # 有伏击配置下的收敛伪影（信息项）
    connection_diffs: list[tuple[str, str, object, object]] = []
    route_problems: list[str] = []
    route_compared = route_identical = route_tiebreak = 0
    route_examples: list[tuple[str, str, str, str]] = []
    route_artifacts: list[str] = []          # 上游自身的路线/代价自相矛盾（伪影，信息项）
    per_level: list[tuple[str, int, int]] = []
    per_config: dict[str, list[int]] = {}
    upstream_seconds = 0.0
    for case in cases:
        began = time.perf_counter()
        try:
            costs, connections, upstream_routes, upstream_ambush = upstream_field(case)
        except Exception as error:                     # noqa: BLE001 —— 上游跑不动就如实记，不算通过
            cost_diffs.append((case["name"], "上游执行失败", type(error).__name__, str(error)[:80]))
            continue
        upstream_seconds += time.perf_counter() - began
        result = csharp.get(case["name"]) or {}
        csharp_costs = result.get("costs") or {}
        csharp_connections = result.get("connections") or {}
        level_diffs = 0
        for node, expected in costs.items():
            actual = csharp_costs.get(node)
            if actual != expected:
                # 上游 `find_path_initial` 用**前沿终止**（`len(new) == len(visited)` 就停），而它的
                # `visited` / `grid_connection` 都是 set（对象身份哈希）→ 收敛到哪个不动点依赖迭代序。
                #   * `has_ambush=False`：每步代价都是 1，等于 BFS，cost 与顺序无关 → 差异就是**硬失败**；
                #   * `has_ambush=True`：C# 侧已改成 **relax 到不动点**（见 CampaignPathfinder 的偏离说明），
                #     所以它给出的是真正最短距离，**不可能比上游更贵**——若更贵就是硬失败（信号：C# 少 relax 了）；
                #     比上游便宜则是上游提前终止的伪影，单列为信息项。
                if case.get("has_ambush") and isinstance(actual, int) and isinstance(expected, int) \
                        and actual < expected:
                    artifacts.append((case["name"], f"cost@{node}", expected, actual))
                else:
                    cost_diffs.append((case["name"], f"cost@{node}", expected, actual))
                level_diffs += 1
                continue
            expected_connection = connections.get(node)
            actual_connection = csharp_connections.get(node)
            if actual_connection == expected_connection:
                continue
            # 连接不同：只允许出现在**等代价的多个最优前驱**之间（上游的择向依赖它自己 set 的迭代序，
            # 用对象身份哈希，本来就不保证跨实现一致）。这时要求**两条路线都最优**：
            # 沿各自的 connection 回溯，累计代价必须都等于该格的 cost。
            connection_diffs.append((case["name"], f"connection@{node}",
                                     expected_connection, actual_connection))
            if node != case["start"]:
                for label, mapping in (("上游", connections), ("C#", csharp_connections)):
                    walked = route_cost(mapping, costs, node, case["start"])
                    if walked is None or walked != costs[node]:
                        route_problems.append(f"{case['name']} {label}@{node}：回溯代价 {walked} ≠ cost {costs[node]}")
            level_diffs += 1
        compared += len(costs)
        per_level.append((case["name"], len(costs), level_diffs))
        bucket = per_config.setdefault(case.get("config", "默认"), [0, 0])
        bucket[0] += len(costs)
        bucket[1] += level_diffs

        # ---- 路线对拍：C# `FindPath` vs 上游 `_find_path` ----
        csharp_routes = result.get("routes") or {}
        for destination, expected_route in (upstream_routes or {}).items():
            actual_route = csharp_routes.get(destination)
            route_compared += 1
            if actual_route == expected_route:
                route_identical += 1
                continue
            # 不同：允许（等代价择向）。校验分两级：
            #   * **自身自洽**：路线首尾正确、每步相邻、按伏击代价累计 == **该侧自己的** cost 场。
            #     两侧都要满足；C# 不自洽是**真问题**，上游不自洽是它的伪影
            #     （它的择向分支会在代价还没收敛时就改写 connection，实测有这种自相矛盾的格）。
            problems_here, artifact_here = [], []
            for label, route, own_costs in (("上游", expected_route, costs), ("C#", actual_route, csharp_costs)):
                if route is None:
                    (problems_here if label == "C#" else artifact_here).append(f"{label} 无路线")
                    continue
                if route[0] != case["start"] and own_costs.get(route[0]) != 0:
                    problems_here.append(f"{label} 起点不是 {case['start']}（{route[0]}）")
                if route[-1] != destination:
                    problems_here.append(f"{label} 终点不是 {destination}（{route[-1]}）")
                total = 0
                for previous, current in zip(route, route[1:]):
                    if current not in neighbours_of(previous):
                        problems_here.append(f"{label} 步 {previous}→{current} 不相邻")
                        break
                    total += 10 if case["has_ambush"] and upstream_ambush.get(current) else 1
                else:
                    if total != own_costs.get(destination):
                        message = (f"{label} 路线代价 {total} ≠ 自己的 cost {own_costs.get(destination)}")
                        (problems_here if label == "C#" else artifact_here).append(message)
            if problems_here:
                route_problems.append(f"{case['name']} → {destination}：{'；'.join(problems_here)}")
            else:
                route_tiebreak += 1
                if artifact_here:
                    route_artifacts.append(f"{case['name']} → {destination}：{'；'.join(artifact_here)}")
                if len(route_examples) < 10:
                    route_examples.append((case["name"], destination,
                                           "/".join(expected_route or []), "/".join(actual_route or [])))

    lines = ["# R5 寻路全库对拍（C# 移植 vs 上游）", "",
             "> 本报告由 `tools/diagnostics/r5_path_sweep.py` 重建，不手写。",
             "> 口径：同一份**声明地图**，每种地图跑**三种配置**（默认 `has_ambush=False,has_enemy=True`、",
             "> `has_ambush=True`、`has_enemy=False`），逐格比 `cost` 与 `connection`。",
             "> **不覆盖**真机识别状态（敌人/机关由识别提供），所以它证明的是**算法移植一致**，不是真机寻路一致。", "",
             f"- 用例数：**{len(per_level)}**（关卡 × 配置；配置见下表）",
             f"- 逐格比较：**{compared}** 格",
             f"- **成本场不一致（无伏击配置）**：**{len(cost_diffs)}** 处（硬指标：这类配置下 cost 与迭代序无关）",
             f"- 有伏击配置的**收敛伪影**：**{len(artifacts)}** 处（**全部是「上游偏大」**：C# relax 到不动点，"
             f"给出的是真正最短距离，不可能比上游更贵；更贵会被判为硬失败）",
             f"- 连接差异：**{len(connection_diffs)}** 处（**只允许等代价的多个最优前驱之间**；上游的择向依赖它自己 ",
             "  `set` 的迭代序、用对象身份哈希，本来就不保证跨实现一致）",
             f"- 路线最优性检查：{'**有** ' + str(len(route_problems)) + ' 处不满足' if route_problems else '两侧回溯代价都等于 cost（通过）'}",
             f"- **路线对拍**（C# `FindPath` vs 上游 `_find_path`）：比较 **{route_compared}** 条 —— "
             f"完全相同 {route_identical}，等代价择向不同但**两侧都最优** {route_tiebreak}，不合法 {len(route_problems)}",
             f"- 用时：C# 侧 {csharp_seconds:.1f}s（含进程启动）/ 上游侧 {upstream_seconds:.1f}s", ""]
    if artifacts:
        lines += ["## 有伏击配置的收敛伪影（前 20 条，信息项）", "", "| 用例 | 位置 | 上游 | C# |", "| --- | --- | --- | --- |"]
        lines += [f"| {name} | {where} | `{expected}` | `{actual}` |" for name, where, expected, actual in artifacts[:20]]
        lines.append("")
    if cost_diffs:
        lines += ["## 成本场不一致（前 20 条）", "", "| 关卡 | 位置 | 上游 | C# |", "| --- | --- | --- | --- |"]
        lines += [f"| {name} | {where} | `{expected}` | `{actual}` |" for name, where, expected, actual in cost_diffs[:20]]
        lines.append("")
    if connection_diffs:
        lines += ["## 连接差异（前 20 条，均为等代价择向）", "", "| 关卡 | 位置 | 上游 | C# |", "| --- | --- | --- | --- |"]
        lines += [f"| {name} | {where} | `{expected}` | `{actual}` |" for name, where, expected, actual in connection_diffs[:20]]
        lines.append("")
    if route_artifacts:
        lines += ["## 上游自身的路线/代价不自洽（前 20 条，伪影）", ""]
        lines += [f"- {item}" for item in route_artifacts[:20]]
        lines.append("")
    if route_problems:
        lines += ["## 路线问题（C# 侧不自洽或不合法）", ""] + [f"- {item}" for item in route_problems[:20]] + [""]
    lines += ["## 每种配置的结果", "", "| 配置 | 逐格比较 | 差异 |", "| --- | --- | --- |"]
    for config, (grids, diffs) in sorted(per_config.items()):
        lines.append(f"| {config} | {grids} | {diffs} |")
    lines.append("")
    histogram = {}
    for _, grids, level_diffs in per_level:
        histogram[level_diffs] = histogram.get(level_diffs, 0) + 1
    lines += ["## 每关差异分布", "", "| 差异数 | 关卡数 |", "| --- | --- |"]
    lines += [f"| {count} | {levels} |" for count, levels in sorted(histogram.items())]
    lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：{len(per_level)} 关 / {compared} 格 / "
          f"成本不一致 {len(cost_diffs)} / 连接差异 {len(connection_diffs)} / 路线问题 {len(route_problems)}")
    if cost_diffs or route_problems:
        for item in (cost_diffs + [(p, "", "", "") for p in route_problems])[:5]:
            print("  -", item)
        return 1
    print("PASS: 全库声明地图上成本场逐格一致；连接差异仅出现在等代价择向，且两侧路线都最优")
    return 0


if __name__ == "__main__":
    sys.exit(main())
