#!/usr/bin/env python3
"""R5 关卡循环对拍：C# 移植的 CampaignBase.run()/execute_a_battle()/battle_function()（离线干跑，无设备）。

用法：
    python tools/diagnostics/verify_r5_loop.py

做的事：
  1. 用夹具 tools/diagnostics/r5-loop-fixture.json 跑产品命令
     `Alas.Server r5-loop --fixture <夹具>`，逐例核对结果类别、详情、轮数、钩子序列、动作与日志；
  2. 用同一份导出独立算出"按 battle_count 应选哪个钩子"（上游默认 battle_function 的规则：
     从 battle_{battle_count} 往回最多 10 个已定义钩子，找不到就用 battle_default），与 C# 选择比对。

只做决策对拍：不连设备、不执行游戏动作。
"""
from __future__ import annotations

import json
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tools" / "diagnostics" / "r5-loop-fixture.json"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def select_hook_expected(chapter: str, level: str, battle_count: int, lookback: int = 10) -> str:
    """按上游默认 battle_function 的规则独立算一遍应选哪个钩子。"""
    payload = json.loads((ROOT / "data" / "campaign" / chapter / f"{level}.json").read_text(encoding="utf-8"))
    methods = {b["method"] for b in payload["campaign"]["battles"]}
    for extra in range(lookback):
        index = battle_count - extra
        if index < 0:
            break
        if f"battle_{index}" in methods:
            return f"battle_{index}"
    return "battle_default"


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    completed = subprocess.run([str(SERVER), "r5-loop", "--fixture", str(FIXTURE)],
                               cwd=ROOT, capture_output=True, text=True, timeout=180,
                               encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout, completed.stderr)
        raise SystemExit(f"r5-loop 退出码 {completed.returncode}")
    payload = json.loads(completed.stdout)
    results = {item["name"]: item for item in payload["cases"]}

    problems: list[str] = []
    checked = cross_checked = 0
    for case in fixture["cases"]:
        result = results.get(case["name"])
        checked += 1
        if result is None:
            problems.append(f"{case['name']}: 命令没有返回这个用例")
            continue
        expect = case.get("expect") or {}
        if "outcome" in expect and result["outcome"] != expect["outcome"]:
            problems.append(f"{case['name']}: 结果 {result['outcome']}，期望 {expect['outcome']}"
                            f"（详情 {result['detail']}）")
        if "detail" in expect and result["detail"] != expect["detail"]:
            problems.append(f"{case['name']}: 详情 {result['detail']!r}，期望 {expect['detail']!r}")
        if "variant" in expect and result["variant"] != expect["variant"]:
            problems.append(f"{case['name']}: 变体 {result['variant']}，期望 {expect['variant']}")
        if "rounds" in expect and len(result["rounds"]) != expect["rounds"]:
            problems.append(f"{case['name']}: 轮数 {len(result['rounds'])}，期望 {expect['rounds']}")
        if "hooks" in expect:
            hooks = [r["hook"] for r in result["rounds"]]
            if hooks != expect["hooks"]:
                problems.append(f"{case['name']}: 钩子序列 {hooks}，期望 {expect['hooks']}")
        if "action_contains" in expect and not any(expect["action_contains"] in a for a in result["actions"]):
            problems.append(f"{case['name']}: 动作 {result['actions']} 不含 {expect['action_contains']!r}")
        if "actions_exclude" in expect and any(expect["actions_exclude"] in a for a in result["actions"]):
            problems.append(f"{case['name']}: 动作 {result['actions']} 不该含 {expect['actions_exclude']!r}")
        if "log_contains" in expect and not any(expect["log_contains"] in line for line in result["logs"]):
            problems.append(f"{case['name']}: 日志不含 {expect['log_contains']!r}")
        if "blocked_contains" in expect:
            blocked = result["rounds"][0].get("blocked") or ""
            if expect["blocked_contains"] not in blocked:
                problems.append(f"{case['name']}: 阻塞原因 {blocked!r} 不含 {expect['blocked_contains']!r}")

        # 与上游钩子选择规则独立对拍：每轮的 battle_count 应用同一规则算出钩子
        config = case.get("config") or {}
        if result["variant"] == "default_hooks":
            for round_info in result["rounds"]:
                expected = select_hook_expected(case["chapter"], case["level"], round_info["battle_count"])
                cross_checked += 1
                if round_info["hook"] != expected:
                    problems.append(
                        f"{case['name']} 第 {round_info['index'] + 1} 轮（battle_count={round_info['battle_count']}）: "
                        f"C# 选 {round_info['hook']}，按上游规则算出 {expected}")

    print(f"[r5-loop] 用例 {checked} 个，其中按 battle_count 独立对拍 {cross_checked} 轮")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 关卡循环决策与上游规则一致（全部为干跑记录，无设备动作）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
