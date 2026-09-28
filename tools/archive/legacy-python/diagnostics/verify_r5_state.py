#!/usr/bin/env python3
"""R5「识别结果 → 引擎状态」对拍（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_state.py

做的事：
  1. `Alas.Server r5-state --chapter campaign_main --level campaign_1_1 --fleet-1 A1 --json`
     核对声明侧：格子数/形状来自关卡导出的 map_data（1-1 是 `G1` 一行七格），
     `ME` 格有 may_enemy、`MB` 格有 may_boss，舰队所在格 cost=0 且全图可达；
  2. 叠一份识别结果（tools/diagnostics/fixtures/detection-sample.json，`grid_flags` 形态）后核对：
     被识别的格子写上运行期标志（is_enemy / is_boss），**认不出的标志**（夹具里故意放 `is_teleporter`）
     必须出现在 unknown_flags 里——不静默丢弃、也不猜语义；
  3. 输出字段与执行夹具的 `grids` 同形（帧 → 状态 → 干跑 这条链的接口）。

只做离线状态构造与核对：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
DETECTION = ROOT / "tools" / "diagnostics" / "fixtures" / "detection-sample.json"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def run_state(*extra: str) -> dict:
    completed = subprocess.run(
        [str(SERVER), "r5-state", "--chapter", "campaign_main", "--level", "campaign_1_1", "--json", *extra],
        cwd=ROOT, capture_output=True, text=True, timeout=180, encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout, completed.stderr)
        raise SystemExit(f"r5-state 退出码 {completed.returncode}")
    return json.loads(completed.stdout)


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    problems: list[str] = []
    grids = {grid["location"]: grid for grid in run_state("--fleet-1", "A1")["grids"]}

    if len(grids) != 7:
        problems.append(f"1-1 应有 7 格（G1 一行），实际 {len(grids)}")
    if not grids["F1"]["may_boss"] and not grids["G1"]["may_boss"]:
        problems.append("`MB` 格应带 may_boss")
    if not grids["F1"]["may_enemy"]:
        problems.append("`ME` 格（F1）应带 may_enemy")
    if grids["A1"]["cost"] != 0:
        problems.append(f"舰队所在格 A1 的 cost 应为 0，实际 {grids['A1']['cost']}")
    if any(grid["cost"] >= 9999 for grid in grids.values()):
        problems.append("1-1 全图应可达（没有 cost=9999 的格子）")
    if not grids["A1"]["is_fleet"]:
        problems.append("A1 应标 is_fleet")

    overlaid = run_state("--fleet-1", "A1", "--detection", str(DETECTION))
    after = {grid["location"]: grid for grid in overlaid["grids"]}
    if not after["F1"]["is_enemy"]:
        problems.append("识别到 F1 有敌人，叠加后 is_enemy 应为真")
    if not after["G1"]["is_boss"]:
        problems.append("识别到 G1 是 boss，叠加后 is_boss 应为真")
    if "is_teleporter" not in overlaid["unknown_flags"]:
        problems.append(f"认不出的标志应报出，实际 unknown_flags={overlaid['unknown_flags']}")
    # 没被识别到的格子保持声明状态（不猜成"没有敌人"，也不被清空）
    if after["D1"]["is_enemy"] or after["D1"]["may_enemy"]:
        problems.append("未被识别的 D1 不应被写成敌人")

    print(f"[r5-state] 声明侧 {len(grids)} 格；叠加识别后 unknown_flags={overlaid['unknown_flags']}")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 声明地图 + 识别叠加 + 成本场构造符合预期")
    return 0


if __name__ == "__main__":
    sys.exit(main())
