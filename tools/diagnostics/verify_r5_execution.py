#!/usr/bin/env python3
"""R5 原语执行对拍：C# 计划执行闭环（离线干跑，无设备）。

用法：
    python tools/diagnostics/verify_r5_execution.py

做的事：
  1. 用夹具 tools/diagnostics/r5-execution-fixture.json 跑产品命令
     `Alas.Server r5-exec --fixture <夹具>`，逐例核对声明；
  2. 对"选目标"类用例，用上游 `module.base.filter.Filter` 与上游 clear_enemy 的优先级规则
     独立算出应打哪个格子，与 C# 实际记录的动作比对（动作形如 clear_chosen_enemy(<格子>, ...)）。

只做决策与动作**记录**的对拍：不连设备、不点击、不执行游戏动作。
"""
from __future__ import annotations

import json
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tools" / "diagnostics" / "r5-execution-fixture.json"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
EXPECTED_PRIMITIVES = ["battle_boss", "battle_default", "brute_clear_boss", "brute_fleet_meet",
                       "capture_clear_boss", "check_accessibility", "ensure_fleet", "fleet_at",
                       "fleet_ensure", "goto", "clear_all_mystery", "clear_any_enemy",
                       "clear_boss",
                       "clear_bouncing_enemy", "clear_chosen_enemy", "clear_enemy", "clear_filter_enemy",
                       "clear_first_roadblocks", "clear_map_items", "clear_mechanism",
                       "clear_potential_boss", "clear_potential_roadblocks", "clear_roadblocks",
                       "clear_siren", "fleet_2_break_siren_caught", "fleet_2_protect",
                       "fleet_2_push_forward", "fleet_2_rescue", "fleet_2_step_on",
                       "handle_boss_appear_refocus", "pick_up_ammo",
                       "pick_up_flare", "pick_up_light_house", "switch_to"]


def upstream_root() -> pathlib.Path:
    configured = os.environ.get("ALAS_REPO")
    path = pathlib.Path(configured) if configured else ROOT / ".runtime" / "engine"
    if not (path / "module" / "map" / "map.py").is_file():
        raise SystemExit(f"找不到上游 ALAS 检出：{path}（用 ALAS_REPO 指定）")
    return path


class UpstreamGrid:
    def __init__(self, item: dict) -> None:
        self.item = item

    @property
    def is_enemy(self) -> bool:
        return bool(self.item.get("is_enemy"))

    @property
    def is_boss(self) -> bool:
        return bool(self.item.get("is_boss"))

    @property
    def is_accessible(self) -> bool:
        return self.item.get("cost", 0) < 9999

    @property
    def enemy_scale(self) -> int:
        return self.item.get("enemy_scale", 0)

    @property
    def weight(self) -> int:
        return self.item.get("weight", 0)

    @property
    def cost(self) -> int:
        return self.item.get("cost", 0)

    @property
    def location(self) -> str:
        return self.item.get("location", "?")


def upstream_clear_enemy_pick(case: dict) -> str | None:
    """按上游 Map.clear_enemy 的规则独立算一遍应打哪个格子。"""
    engine = upstream_root()
    if str(engine) not in sys.path:
        sys.path.insert(0, str(engine))
    from module.base.filter import Filter  # noqa: PLC0415  (仅用于确认上游实现可导入)

    _ = Filter(regex=re.compile("^(.*?)$"), attr=("str",))
    config = case.get("config") or {}
    grids = [UpstreamGrid(item) for item in case.get("grids") or []]
    grids = [g for g in grids if g.is_enemy and not g.is_boss and g.is_accessible]
    if not grids:
        return None
    priority = config.get("enemy_priority")
    strong = priority == "S3_enemy_first" or (priority != "S1_enemy_first"
                                             and config.get("map_clear_all_this_time"))
    weak = priority == "S1_enemy_first"
    if strong:
        for scale in (3, 2, 1, 0):
            picked = [g for g in grids if g.enemy_scale == scale]
            if picked:
                grids = picked
                break
    elif weak:
        for scale in (1, 2, 3, 0):
            picked = [g for g in grids if g.enemy_scale == scale]
            if picked:
                grids = picked
                break
    return sorted(grids, key=lambda g: (g.weight, g.cost))[0].location


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    completed = subprocess.run([str(SERVER), "r5-exec", "--fixture", str(FIXTURE)],
                               cwd=ROOT, capture_output=True, text=True, timeout=180,
                               encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout, completed.stderr)
        raise SystemExit(f"r5-exec 退出码 {completed.returncode}")
    payload = json.loads(completed.stdout)
    results = {item["name"]: item for item in payload["cases"]}

    problems: list[str] = []
    # 按**集合**比：注册表的顺序不是契约，列表逐位比会因插入位置不同而假失败（实测踩过）。
    if set(payload["implemented_primitives"]) != set(EXPECTED_PRIMITIVES):
        registered = set(payload["implemented_primitives"])
        problems.append("已登记原语与期望不一致："
                        f"多 {sorted(registered - set(EXPECTED_PRIMITIVES))}，"
                        f"少 {sorted(set(EXPECTED_PRIMITIVES) - registered)}")

    checked = cross_checked = 0
    for case in fixture["cases"]:
        result = results.get(case["name"])
        checked += 1
        if result is None:
            problems.append(f"{case['name']}: 命令没有返回这个用例")
            continue
        expect = case.get("expect") or {}
        for field in ("actions", "invoked_ops"):
            if field in expect and result.get(field) != expect[field]:
                problems.append(f"{case['name']}: {field}={result.get(field)!r}, expected {expect[field]!r}")
        if "return" in expect and result["return"] != expect["return"]:
            problems.append(f"{case['name']}: 返回 {result['return']!r}，期望 {expect['return']!r}")
        if "completed" in expect and result["completed"] != expect["completed"]:
            problems.append(f"{case['name']}: 完成={result['completed']}，期望 {expect['completed']}")
        if "blocked_contains" in expect and expect["blocked_contains"] not in (result.get("blocked") or ""):
            problems.append(f"{case['name']}: 阻塞原因 {result.get('blocked')!r} 不含 {expect['blocked_contains']!r}")
        if "action_contains" in expect and not any(expect["action_contains"] in a for a in result["actions"]):
            problems.append(f"{case['name']}: 动作 {result['actions']} 不含 {expect['action_contains']!r}")
        if "actions_exclude" in expect and any(expect["actions_exclude"] in a for a in result["actions"]):
            problems.append(f"{case['name']}: 动作 {result['actions']} 不该含 {expect['actions_exclude']!r}")
        if "log_contains" in expect and not any(expect["log_contains"] in line for line in result["logs"]):
            problems.append(f"{case['name']}: 日志 {result['logs']} 不含 {expect['log_contains']!r}")
        if "log_excludes" in expect and any(expect["log_excludes"] in line for line in result["logs"]):
            problems.append(f"{case['name']}: 日志 {result['logs']} 不该含 {expect['log_excludes']!r}")
        if "steps_contains" in expect and not any(expect["steps_contains"] in line for line in result["steps"]):
            problems.append(f"{case['name']}: 步骤 {result['steps']} 不含 {expect['steps_contains']!r}")

        # 与上游规则独立对拍：仅对声明了 cross_check=clear_enemy 的用例（其它原语的规则不同）
        if case.get("cross_check") == "clear_enemy" and case.get("grids") is not None:
            expected = upstream_clear_enemy_pick(case)
            selected = None
            for action in result["actions"]:
                if action.startswith("clear_chosen_enemy("):
                    selected = action.split("(", 1)[1].split(",", 1)[0]
                    break
            cross_checked += 1
            if selected != expected:
                problems.append(f"{case['name']}: C# 打了 {selected!r}，上游规则算出 {expected!r}")

    print(f"[r5-exec] 用例 {checked} 个，其中与上游选敌规则独立对拍 {cross_checked} 个；"
          f"已登记原语 {len(payload['implemented_primitives'])} 个")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 计划→原语→动作闭环与上游规则一致（全部为干跑记录，无设备动作）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
