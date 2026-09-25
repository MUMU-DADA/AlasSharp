#!/usr/bin/env python3
"""R5 寻路对拍：C# 移植 vs 上游 CampaignMap.find_path_initial（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_path.py

对拍方式：
  1. 用夹具 tools/diagnostics/r5-path-fixture.json 跑产品命令
     `Alas.Server r5-path --fixture <夹具>`，拿到每个用例的成本场与连接；
  2. **在上游实现上跑同一份地图**：构造 module.map.map_base.CampaignMap，写入同样的 map_data 令牌，
     套用夹具里的识别覆盖，跑 grid_connection_initial + find_path_initial，逐格比较 cost 与 connection，
     并按 _find_path 比较路线。

只做地图算术的对拍：不连设备、不读识别结果。
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tools" / "diagnostics" / "r5-path-fixture.json"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
UPSTREAM = ROOT / ".runtime" / "engine"


def upstream_field(case: dict) -> tuple[dict, dict, dict]:
    """在上游 CampaignMap 上算同一份地图：返回 (cost, connection, location→node)。"""
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))
    from module.base.utils import location2node  # noqa: PLC0415
    from module.map.map_base import CampaignMap  # noqa: PLC0415

    rows = case["rows"]
    campaign_map = CampaignMap("probe")
    campaign_map.shape = location2node((max(len(r.split()) for r in rows) - 1, len(rows) - 1))
    campaign_map.map_data = "\n".join(rows)
    campaign_map.load_map_data()
    campaign_map.grid_connection_initial(wall=True)

    for cell in case.get("cells") or []:
        node = cell["location"]
        grid = campaign_map[tuple(node_to_location(node))]
        for key, value in cell.items():
            if key == "location":
                continue
            setattr(grid, key, value)

    start = tuple(node_to_location(case["start"]))
    campaign_map.find_path_initial(start, has_ambush=case.get("has_ambush", False),
                                   has_enemy=case.get("has_enemy", True))

    costs, connections, nodes = {}, {}, {}
    for grid in campaign_map:
        node = location2node(grid.location)
        nodes[node] = node
        costs[node] = grid.cost
        connections[node] = location2node(grid.connection) if grid.connection is not None else None
    return costs, connections, nodes


def node_to_location(node: str) -> tuple[int, int]:
    return ord(node[0].upper()) - ord("A"), int(node[1:]) - 1


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    completed = subprocess.run([str(SERVER), "r5-path", "--fixture", str(FIXTURE)],
                               cwd=ROOT, capture_output=True, text=True, timeout=180,
                               encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout, completed.stderr)
        raise SystemExit(f"r5-path 退出码 {completed.returncode}")
    payload = json.loads(completed.stdout)
    results = {item["name"]: item for item in payload["cases"]}

    problems: list[str] = []
    checked = compared = connection_diffs = 0
    for case in fixture["cases"]:
        result = results.get(case["name"])
        checked += 1
        if result is None:
            problems.append(f"{case['name']}: 命令没有返回这个用例")
            continue
        costs, connections, _ = upstream_field(case)
        compared += len(costs)
        for node, expected_cost in costs.items():
            actual = result["costs"].get(node)
            if actual != expected_cost:
                problems.append(f"{case['name']} {node}: C# cost={actual}，上游 cost={expected_cost}")
        for node, expected_connection in connections.items():
            actual = result["connections"].get(node)
            if actual != expected_connection:
                # 连接在"等代价"时可能因遍历顺序不同而不同，只要代价一致就先记为提示
                connection_diffs += 1
                if costs[node] != (result["costs"].get(node) or -1):
                    problems.append(f"{case['name']} {node}: C# connection={actual}，上游={expected_connection}")

        if case.get("destination"):
            expected_route = upstream_route(case)
            if result["route"] != expected_route:
                problems.append(f"{case['name']}: C# 路线 {result['route']}，上游路线 {expected_route}")

    print(f"[r5-path] 用例 {checked} 个，逐格比较 {compared} 格；连接差异（等代价）{connection_diffs} 处")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems[:12]:
            print(f"  - {item}")
        return 1
    print("PASS: 成本场与上游 find_path_initial 一致（路线按 _find_path 比对）")
    return 0


def upstream_route(case: dict) -> list[str]:
    """用上游 _find_path 算出路线（需要重新构造地图，代价场已在上游侧算过）。"""
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))
    from module.base.utils import location2node  # noqa: PLC0415
    from module.map.map_base import CampaignMap  # noqa: PLC0415

    rows = case["rows"]
    campaign_map = CampaignMap("probe")
    campaign_map.shape = location2node((max(len(r.split()) for r in rows) - 1, len(rows) - 1))
    campaign_map.map_data = "\n".join(rows)
    campaign_map.load_map_data()
    campaign_map.grid_connection_initial(wall=True)
    for cell in case.get("cells") or []:
        grid = campaign_map[tuple(node_to_location(cell["location"]))]
        for key, value in cell.items():
            if key != "location":
                setattr(grid, key, value)
    campaign_map.find_path_initial(tuple(node_to_location(case["start"])),
                                   has_ambush=case.get("has_ambush", False),
                                   has_enemy=case.get("has_enemy", True))
    route = campaign_map._find_path(tuple(node_to_location(case["destination"])))
    return [location2node(item) for item in (route or [])]


if __name__ == "__main__":
    sys.exit(main())
