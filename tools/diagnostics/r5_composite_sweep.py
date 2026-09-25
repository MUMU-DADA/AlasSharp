#!/usr/bin/env python3
"""R5 复合原语扫描：C# 原语 vs **上游真实方法**（离线，无设备）。

用法：
    python tools/diagnostics/r5_composite_sweep.py [--states 60] [--seed 13]

覆盖 `clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` /
`clear_roadblocks` / `clear_potential_roadblocks` 六个**复合原语**的判定：
它们不只是选择器，还带配置分支（`EnemyPriority_EnemyScaleBalanceWeight`、`MAP_CLEAR_ALL_THIS_TIME`、
`MAP_HAS_SIREN`/`MAP_HAS_FORTRESS`、`FLEET_2` 改排序键）、可能的 boss 兜底路径等。

对拍方式：
  * 上游侧：用**真实 `CampaignMap`**（`load_map_data` 后逐格设置标志）当 `self.map`，
    把 `Map.clear_enemy` 等**未绑定的真实方法**绑到一个替身上；替身只提供 `config` 与
    **记录型动作**（`clear_chosen_enemy` / `goto` / `submarine_move_near_boss`），
    于是跑的是上游自己的判定，记录到的就是"它打了哪一格"；
  * C# 侧：`Alas.Server r5-select` 的 `primitive_clear_*` 种类（同一份状态 + 同一份配置），
    目标从记录宿主的动作里取。

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
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-composite-sweep.md"
WORK = ROOT / ".runtime" / "r5-probe"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
UPSTREAM = ROOT / ".runtime" / "engine"

GENRES = ["Light", "Main", "Carrier", "Treasure"]
PRIMITIVES = ["clear_enemy", "clear_any_enemy", "clear_siren", "clear_boss",
              "clear_roadblocks", "clear_potential_roadblocks"]
ROADBLOCK_PRIMITIVES = {"clear_roadblocks", "clear_potential_roadblocks"}
CONFIGS = [
    {"enemy_priority": None},
    {"enemy_priority": "S3_enemy_first"},
    {"enemy_priority": "S1_enemy_first"},
    {"map_clear_all_this_time": True},
    {"map_has_siren": True},
    {"map_has_siren": True, "fleet_2": True},
    {"map_has_fortress": True},
    {"map_has_siren": True, "map_has_fortress": True, "fleet_2": True},
]


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
                    "may_boss": rng.random() < 0.2,
                    "is_siren": rng.random() < 0.15,
                    "is_fortress": rng.random() < 0.1,
                    "is_caught_by_siren": rng.random() < 0.1,
                    "enemy_scale": rng.choice([1, 2, 3]),
                    "enemy_genre": rng.choice(GENRES),
                    "weight": rng.choice([0, 10, 20, 30, 40, 50, 60, 70, 80, 90]),
                    "cost": cost,
                    "cost_1": 9999 if rng.random() < 0.3 else rng.randint(1, 28),
                    "cost_2": 9999 if rng.random() < 0.3 else rng.randint(1, 28),
                })
        # 上游 `find_path_initial` 会把舰队格标 `is_fleet`（`Fleet.find_path_initial` 里做的），
        # 而 `potential_roadblocks` 会跳过含舰队的块 —— 夹具必须带上这个标志，否则两边状态不同
        fleet_cell = grids[0]["location"]
        for kind in PRIMITIVES:
            for config_index, config in enumerate(CONFIGS):
                case = {
                    "name": f"state{index}-{kind}-c{config_index}",
                    "kind": f"primitive_{kind}",
                    "grids": [dict(grid, is_fleet=grid["location"] == fleet_cell) for grid in grids],
                    # 舰队位置要与上游替身一致：1 队在第一格、没有 2 队（兜底分支的搜索起点靠它）
                    "fleet_1_location": fleet_cell,
                    "fleet_current_index": 1,
                    **config,
                }
                if kind in ROADBLOCK_PRIMITIVES:
                    # 路段：每行切成"3 格一块 + 1 格一块"两条路段，够覆盖 roadblocks/potential_roadblocks
                    rows: dict[int, list[str]] = {}
                    for grid in grids:
                        rows.setdefault(int(grid["location"][1:]), []).append(grid["location"])
                    blocks = [row[:3] for row in rows.values() if row]
                    singles = [[row[3]] for row in rows.values() if len(row) > 3]
                    case["roads"] = [[block] for block in blocks] + [[block] for block in singles]
                cases.append(case)
    return cases


class RecordingStub:
    """替身：只提供 config / 舰队属性 / 记录型动作；`map` 用真实 `CampaignMap`。

    `battle_count` 在每次 `clear_chosen_enemy` 后自增——与 C# 侧 `RecordingCampaignHost` 的假定一致
    （"选中的敌人会被打掉"），否则 `clear_potential_boss` 这类"打一架看 battle_count 有没有涨"的
    分支两边会走成不同路径（那是替身差异，不是引擎差异）。
    """

    def __init__(self, campaign_map, config, first_location="A1"):
        self.map = campaign_map
        self.config = config
        self.battle_count = 0
        # `brute_find_roadblocks`（兜底分支）会读这些舰队状态；给"1 队在第一格、没有 2 队"的最小前提
        self.fleet_current_index = 1
        from module.base.utils import node2location  # noqa: PLC0415
        self.fleet_1_location = node2location(first_location)
        self.fleet_2_location = tuple()
        self.calls: list[tuple[str, str]] = []
        self.select_grids = self._select_grids

    def fleet_ensure(self, index=1):
        self.fleet_current_index = index

    @staticmethod
    def _select_grids(grids, **kwargs):
        from module.map.map import Map  # noqa: PLC0415
        return Map.select_grids(grids, **kwargs)

    # 舰队属性：上游这几个属性会先 fleet_ensure 再返回 self，这里只需要"返回 self"的语义
    @property
    def fleet_1(self):
        return self

    @property
    def fleet_2(self):
        return self

    @property
    def fleet_boss(self):
        return self

    @property
    def fleet_boss_index(self):
        return 2 if (self.config.FLEET_BOSS == 2 and self.config.FLEET_2) else 1

    def clear_chosen_enemy(self, grid, expected="", fleet=None):
        self.calls.append(("clear_chosen_enemy", str(grid)))
        self.battle_count += 1
        return True

    def goto(self, location, expected="", **kwargs):
        self.calls.append(("goto", str(location)))

    def submarine_move_near_boss(self, boss):
        self.calls.append(("submarine_move_near_boss", str(boss)))

    def show_select_grids(self, *args, **kwargs):
        return None

    def brute_find_roadblocks(self, grid, fleet=None):
        from module.map.map import Map  # noqa: PLC0415
        return Map.brute_find_roadblocks(self, grid, fleet=fleet)

    def clear_potential_boss(self):
        from module.map.map import Map  # noqa: PLC0415
        return Map.clear_potential_boss(self)

    def find_path_initial(self, location=None, has_ambush=True, has_enemy=True):
        """上游有**两个同名方法**：`Fleet.find_path_initial(self)`（无参、读自己的舰队位置）与
        `Map.find_path_initial(self, location, …)`。`brute_find_roadblocks` 调的是前者——
        所以无参时绑**真实的 Fleet 变体**，带参时转发到真实 `CampaignMap` 上的方法。
        """
        from module.map.fleet import Fleet  # noqa: PLC0415
        if location is None:
            return Fleet.find_path_initial(self)
        return self.map.find_path_initial(location, has_ambush=has_ambush, has_enemy=has_enemy)

    @property
    def fleet_current(self):
        return self.fleet_2_location if self.fleet_current_index == 2 else self.fleet_1_location


def upstream_target(case: dict) -> tuple[str | None, bool | None]:
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))
    from module.base.utils import location2node  # noqa: PLC0415
    from module.map.map_base import CampaignMap  # noqa: PLC0415
    from module.map.map import Map  # noqa: PLC0415

    rows = {}
    for grid in case["grids"]:
        rows.setdefault(grid["location"][1:], []).append(grid)
    height = max(int(key) for key in rows)
    width = max(ord(grid["location"][0]) - 65 for grid in case["grids"]) + 1
    map_data = "\n".join(" ".join("--" for _ in range(width)) for _ in range(height))

    campaign_map = CampaignMap("sweep")
    campaign_map.shape = location2node((width - 1, height - 1))
    campaign_map.map_data = map_data
    campaign_map.load_map_data()
    # 上游 `map_init` 会先建网格连接；替身手工补上（`wall=False`，与 C# 侧的四邻接一致），
    # 否则 `brute_find_roadblocks → find_path_initial` 会在 `grid_connection[...]` 上 KeyError（实测）
    campaign_map.grid_connection_initial(wall=False)
    for grid in case["grids"]:
        info = campaign_map[tuple((ord(grid["location"][0]) - 65, int(grid["location"][1:]) - 1))]
        info.is_enemy = bool(grid["is_enemy"])
        info.is_boss = bool(grid["is_boss"])
        info.may_boss = bool(grid["may_boss"])
        info.is_siren = bool(grid["is_siren"])
        info.is_fortress = bool(grid["is_fortress"])
        info.is_caught_by_siren = bool(grid["is_caught_by_siren"])
        info.enemy_scale = int(grid["enemy_scale"])
        info.enemy_genre = grid["enemy_genre"]
        info.weight = int(grid["weight"])
        info.cost = int(grid["cost"])
        info.cost_1 = int(grid["cost_1"])
        info.cost_2 = int(grid["cost_2"])

    class Config:
        def __init__(self, payload):
            self._payload = payload

        def __getattribute__(self, name):
            payload = object.__getattribute__(self, "_payload")
            if name in payload:
                return payload[name]
            return {
                "EnemyPriority_EnemyScaleBalanceWeight": "S3_enemy_first",
                "MAP_CLEAR_ALL_THIS_TIME": False,
                "MAP_HAS_SIREN": False,
                "MAP_HAS_FORTRESS": False,
                "FLEET_2": False,
            }.get(name, False)

    config = Config({
        "EnemyPriority_EnemyScaleBalanceWeight": case.get("enemy_priority") or "",
        "MAP_CLEAR_ALL_THIS_TIME": bool(case.get("map_clear_all_this_time")),
        "MAP_HAS_SIREN": bool(case.get("map_has_siren")),
        "MAP_HAS_FORTRESS": bool(case.get("map_has_fortress")),
        "FLEET_2": bool(case.get("fleet_2")),
    })
    stub = RecordingStub(campaign_map, config, case["grids"][0]["location"])
    # **先跑一次寻路**再调原语：真实运行里 cost 场是 `map_init` / `full_scan` 之后就已算好的，
    # 上游原语读的就是这份 cost。少了这一步，上游看到的是夹具里那些**随便填的 cost**，
    # 而 C# 内部会自己算成本场 → 两边状态根本不同（实测：8 处 clear_potential_boss 假不一致）。
    stub.find_path_initial()
    # 成本场要在**调原语之前**快照：上游原语自己会改它（路障兜底临时清敌人标记重算寻路），
    # 跑完再读就会把"被改过的 cost"写给 C#（实测：clear_any_enemy 假不一致）。
    costs = {}
    for grid in case["grids"]:
        info = campaign_map[tuple((ord(grid["location"][0]) - 65, int(grid["location"][1:]) - 1))]
        costs[grid["location"]] = (int(info.cost), int(info.cost_1), int(info.cost_2))
    method = getattr(Map, case["kind"].replace("primitive_", ""))
    if case["kind"].replace("primitive_", "") in ROADBLOCK_PRIMITIVES:
        from module.map.map_grids import RoadGrids  # noqa: PLC0415
        roads = []
        for road in case.get("roads") or []:
            blocks = []
            for block in road:
                blocks.append([campaign_map[tuple((ord(location[0]) - 65, int(location[1:]) - 1))]
                               for location in block])
            roads.append(RoadGrids(blocks))
        result = method(stub, roads)
    else:
        result = method(stub)
    target = next((location for name, location in stub.calls
                   if name in ("clear_chosen_enemy", "goto")), None)
    return target, result, costs


# 只比**排序键**：上游 `SelectedGrids.add` 的 `set` 打乱的是"谁先被加入"，
# 于是等 weight/cost 的格子可能来自不同过滤组（如 enemy 与 fortress），这同样是不可复现的集合序伪影。
RANK_KEYS = ("weight", "cost")


def find_grid(case: dict, location: str | None) -> dict | None:
    if location is None:
        return None
    return next((grid for grid in case["grids"] if grid["location"] == location), None)


def same_rank(left: dict, right: dict) -> bool:
    """两个格子是否"同价位"：排序键与标志都一样（含可达性），只是身份不同。"""
    if any(left.get(key) != right.get(key) for key in RANK_KEYS):
        return False
    return (left.get("cost", 9999) < 9999) == (right.get("cost", 9999) < 9999)


def main() -> int:
    parser = argparse.ArgumentParser(description="R5 复合原语扫描")
    # 默认 20 个状态：上游 `clear_potential_boss` 的路障兜底会**指数枚举**敌人子集
    # （`itertools.product(enemies, repeat)`），状态一多整体就超出可接受时长（实测 60 状态 >10 分钟）。
    parser.add_argument("--states", type=int, default=20)
    parser.add_argument("--seed", type=int, default=13)
    options = parser.parse_args()
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")

    cases = build_cases(options.states, options.seed)

    # 第一遍：跑上游拿期望，并把**上游算出的成本场**写回夹具，
    # 这样 C# 侧读到的 cost/cost_1/cost_2 与上游看到的完全一致（否则是两套状态）。
    expected_by_name: dict[str, str | None] = {}
    skipped: dict[str, int] = {}
    for case in cases:
        try:
            target, _, costs = upstream_target(case)
        except Exception as error:                     # noqa: BLE001 —— 上游跑不动就如实记，不算通过
            key = f"上游执行失败：{type(error).__name__}: {error}"[:110]
            skipped[key] = skipped.get(key, 0) + 1
            continue
        expected_by_name[case["name"]] = target
        for grid in case["grids"]:
            if grid["location"] in costs:
                grid["cost"], grid["cost_1"], grid["cost_2"] = costs[grid["location"]]

    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / "composite-sweep-fixture.json"
    fixture.write_text(json.dumps({"cases": cases}, ensure_ascii=False), encoding="utf-8", newline="\n")

    completed = subprocess.run([str(SERVER), "r5-select", "--fixture", str(fixture)],
                               cwd=ROOT, capture_output=True, text=True, timeout=1800,
                               encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout[-1500:], completed.stderr[-1500:])
        raise SystemExit(f"r5-select 退出码 {completed.returncode}")
    results = {item["name"]: item for item in json.loads(completed.stdout)["cases"]}

    mismatches: list[tuple[str, str, str]] = []
    artifacts: list[tuple[str, str, str]] = []
    compared = 0
    for case in cases:
        result = results.get(case["name"])
        if result is None:
            mismatches.append((case["name"], case["kind"], "命令没返回这个用例"))
            continue
        if case["name"] not in expected_by_name:
            continue                                   # 上游侧跳过过，如实计入 skipped
        expected = expected_by_name[case["name"]]
        compared += 1
        if result["selected"] != expected:
            # 上游 `SelectedGrids.add` 是 `set(self.grids + grids.grids)`——**走哈希集合**，
            # 于是"追加顺序"其实是身份哈希序：等 (weight, cost) 的格子谁排前面不可复现。
            # 所以两侧选了**同价位等价格**（同 weight/cost/标志/可达性）时不算不一致，单列伪影。
            left = find_grid(case, result["selected"])
            right = find_grid(case, expected)
            if left is not None and right is not None and same_rank(left, right):
                artifacts.append((case["name"], result["selected"], expected))
                continue
            mismatches.append((case["name"], f"{case['kind']} {json.dumps({k: v for k, v in case.items() if k not in ('grids', 'name', 'kind')}, ensure_ascii=False)}",
                               f"C# {result['selected']!r} vs 上游 {expected!r}"))

    lines = ["# R5 复合原语扫描（C# 原语 vs 上游真实方法）", "",
             "> 本报告由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。",
             "> 覆盖：`clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` /", 
             "> `clear_roadblocks` / `clear_potential_roadblocks` 的判定（含路段语义），",
             "> 含配置分支（优先级、全清、塞壬/要塞、FLEET_2 改排序键）；上游侧跑的是**它自己的方法**。", "",
             f"- 状态数：**{options.states}**（seed={options.seed}，每种状态 × {len(PRIMITIVES)} 原语 × {len(CONFIGS)} 配置）",
             f"- 用例数：**{len(cases)}**；实际比较 **{compared}**",
             f"- 不一致：**{len(mismatches)}**",
             f"- 等价位**集合序伪影**：**{len(artifacts)}** 处（上游 `SelectedGrids.add` 走 `set`，"
             f"等 weight/cost 的格子谁在前不可复现；两侧都选中同价位格子）", ""]
    if artifacts:
        lines += ["## 集合序伪影（前 10 条，信息项）", "", "| 用例 | C# | 上游 |", "| --- | --- | --- |"]
        lines += [f"| {name} | `{left}` | `{right}` |" for name, left, right in artifacts[:10]]
        lines.append("")
    if mismatches:
        lines += ["## 不一致（前 20 条）", "", "| 用例 | 配置 | 差异 |", "| --- | --- | --- |"]
        lines += [f"| {name} | `{detail}` | {diff} |" for name, detail, diff in mismatches[:20]]
        lines.append("")
    if skipped:
        lines += ["## 跳过（如实列出原因）", "", "| 原因 | 次数 |", "| --- | --- |"]
        lines += [f"| {reason} | {count} |" for reason, count in sorted(skipped.items())]
        lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：比较 {compared} 例 / 不一致 {len(mismatches)} / 跳过 {sum(skipped.values())}")
    for item in mismatches[:5]:
        print("  -", item)
    for reason, count in list(skipped.items())[:2]:
        print(f"  跳过 {count} 次：{reason}")
    return 1 if mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
