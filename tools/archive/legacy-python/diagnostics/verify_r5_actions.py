#!/usr/bin/env python3
"""R5 原语级动作轨迹对拍：上游运行日志 → 我们的原语名（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_actions.py

做的事：
  1. 用夹具日志 tools/diagnostics/fixtures/actions-sample.log（按上游真实日志格式写：
     `<<< CLEAR FILTER ENEMY >>>` 表头 + `Clear enemy: F1`、`Pick up ammo: B2`、
     `Fleet_2 step on C3`、`Brute clear BOSS`、`Enemy roadblock: D2` 等）跑产品命令
     `Alas.Server r5-actions --log <日志> --chapter <章> --level <关> --json`；
  2. 核对解析出的动作序列、原语集合、"C# 未实现"清单与"认不出的动作行"。

夹具里的日志格式取自上游 `module/map/map.py` / `fleet.py` / `campaign_base.py` 里真实存在的
`logger.hr(...)` / `logger.info(...)`，不是编造的字段。

只做日志解析与对照：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tools" / "diagnostics" / "fixtures" / "actions-sample.log"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"

EXPECTED_ACTIONS = [
    ("clear_all_mystery", None),
    ("pick_up_ammo", "B2"),
    ("clear_filter_enemy", None),
    ("clear_chosen_enemy", "F1"),
    ("fleet_2_push_forward", None),
    ("fleet_2_step_on", "C3"),
    ("brute_clear_boss", None),
    ("brute_clear_boss", "D2"),
    ("clear_boss", None),
    ("clear_chosen_enemy", "G1"),
]
EXPECTED_PRIMITIVES = ["clear_all_mystery", "pick_up_ammo", "clear_filter_enemy", "clear_chosen_enemy",
                       "fleet_2_push_forward", "fleet_2_step_on", "brute_clear_boss", "clear_boss"]


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    completed = subprocess.run(
        [str(SERVER), "r5-actions", "--log", str(FIXTURE),
         "--chapter", "campaign_main", "--level", "campaign_1_1", "--json"],
        cwd=ROOT, capture_output=True, text=True, timeout=180, encoding="utf-8", errors="replace")
    if completed.returncode not in (0, 1):
        print(completed.stdout, completed.stderr)
        raise SystemExit(f"r5-actions 退出码 {completed.returncode}")
    payload = json.loads(completed.stdout)

    problems: list[str] = []
    actions = [(item["primitive"], item["target"]) for item in payload["actions"]]
    if actions != EXPECTED_ACTIONS:
        problems.append(f"动作序列不符：\n    实际 {actions}\n    期望 {EXPECTED_ACTIONS}")
    if payload["primitives"] != EXPECTED_PRIMITIVES:
        problems.append(f"原语集合 {payload['primitives']}，期望 {EXPECTED_PRIMITIVES}")
    if payload["missing"]:
        problems.append(f"夹具用到的原语里有未实现的：{payload['missing']}")
    # 夹具里那条 `BOSS not detected, ...` 表里没有对应规则 → 必须落进"认不出的动作行"
    unknown = payload["unknown_markers"]
    if not any("BOSS not detected" in line for line in unknown):
        problems.append(f"`BOSS not detected` 应被记为认不出的动作行，实际 {unknown}")

    print(f"[r5-actions] 动作 {len(actions)} 次 / 原语 {len(payload['primitives'])} 个 / "
          f"未实现 {len(payload['missing'])} 个 / 认不出的行 {len(unknown)} 条")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 原语级动作轨迹解析与 C# 覆盖对照符合预期")
    return 0


if __name__ == "__main__":
    sys.exit(main())
