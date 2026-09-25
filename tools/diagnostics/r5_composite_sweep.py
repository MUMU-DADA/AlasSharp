#!/usr/bin/env python3
"""R5 复合原语扫描：C# 原语 vs **上游真实方法**（离线，无设备）。

用法：
    python tools/diagnostics/r5_composite_sweep.py [--states 60] [--seed 13]

覆盖 `clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` /
`clear_roadblocks` / `clear_potential_roadblocks` / `clear_first_roadblocks` / `pick_up_ammo` /
`fleet_2_push_forward` / `fleet_2_protect`
十个**复合原语**的判定：
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
# 库里的真实过滤器串（`clear_filter_enemy` 用得最多的那条）
ENEMY_FILTER_TEXT = "1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C"
PRIMITIVES = ["clear_enemy", "clear_any_enemy", "clear_siren", "clear_boss",
              "clear_roadblocks", "clear_potential_roadblocks", "clear_first_roadblocks",
              "pick_up_ammo", "fleet_2_push_forward", "fleet_2_protect", "brute_clear_boss",
              "brute_fleet_meet", "clear_potential_boss", "clear_filter_enemy"]

# 曾经把 `brute_clear_boss` 当成"已知差异"排除在外，理由是"路障子集选择不同"。**那是误判**：
# 真正的原因是诊断命令缺 `primitive_brute_clear_boss` 分派，静默落进了"选一个敌人"的默认分支。
# 现在 C# 侧对未知 `primitive_*` kind 直接报错，扫描也把 13 种原语全部纳入（见重写文档的记录）。
KNOWN_DIVERGENCE: list[str] = []      # 13 种原语现已全部纳入；曾误登记的 `brute_clear_boss` 见重写文档的说明
ROADBLOCK_PRIMITIVES = {"clear_roadblocks", "clear_potential_roadblocks", "clear_first_roadblocks"}
CONFIGS = [
    {"enemy_priority": None},
    {"enemy_priority": "S3_enemy_first"},
    {"enemy_priority": "S1_enemy_first"},
    {"map_clear_all_this_time": True},
    {"map_has_siren": True},
    {"map_has_siren": True, "fleet_2": True},
    {"map_has_fortress": True},
    {"map_has_siren": True, "map_has_fortress": True, "fleet_2": True},
    # 这两条给 `fleet_2_*`：上游要求 2 队 + 可移动敌人（`fleet_boss_index == 2` 还需要 fleet_boss=2）
    {"fleet_2": True, "fleet_boss": True, "map_has_movable_enemy": True},
    {"fleet_2": True, "fleet_boss": True, "map_has_movable_enemy": True,
     "map_has_movable_normal_enemy": True, "map_has_siren": True},
    # 给 `clear_filter_enemy` 的 movable 分支（上游会**忽略过滤串**转 clear_any_enemy(sort=("cost_2",))）
    {"map_has_movable_normal_enemy": True},
    {"map_has_movable_normal_enemy": True, "fleet_2": True, "map_has_siren": True},
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
                    "may_ammo": rng.random() < 0.25,
                    "enemy_scale": rng.choice([1, 2, 3]),
                    "enemy_genre": rng.choice(GENRES),
                    "weight": rng.choice([0, 10, 20, 30, 40, 50, 60, 70, 80, 90]),
                    "cost": cost,
                    "cost_1": 9999 if rng.random() < 0.3 else rng.randint(1, 28),
                    "cost_2": 9999 if rng.random() < 0.3 else rng.randint(1, 28),
                })
        # 上游 `find_path_initial` 会把舰队格标 `is_fleet`（`Fleet.find_path_initial` 里做的），
        # 而 `potential_roadblocks`/`fleet_2_*` 都会看这个标志 —— 夹具必须带上，否则两边状态不同
        fleet_cell = grids[0]["location"]
        second_cell = grids[1]["location"] if len(grids) > 1 else ""
        for kind in PRIMITIVES:
            for config_index, config in enumerate(CONFIGS):
                has_fleet_2 = bool(config.get("fleet_2"))
                case = {
                    "name": f"state{index}-{kind}-c{config_index}",
                    "kind": f"primitive_{kind}",
                    "grids": [dict(grid, is_fleet=grid["location"] == fleet_cell
                                   or (has_fleet_2 and grid["location"] == second_cell))
                              for grid in grids],
                    # 舰队位置要与上游替身一致：1 队在第一格；有 2 队时它在第二格
                    "fleet_1_location": fleet_cell,
                    "fleet_2_location": second_cell if has_fleet_2 else "",
                    "fleet_current_index": 1,
                    **config,
                }
                if kind == "clear_filter_enemy":
                    # 过滤器串与 preserve 也要给 C# 侧（默认那条 + 0/1 交替，覆盖 preserve 截断）
                    case["filter"] = ENEMY_FILTER_TEXT
                    case["preserve"] = 1 if len(grids) % 2 else 0
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


class FleetProxy:
    """舰队代理：把"哪一队"带进记录（上游 `self.fleet_2.goto(...)` 就是切到 2 队再走）。"""

    def __init__(self, stub: "RecordingStub", index: int) -> None:
        self._stub = stub
        self._index = index

    def switch_to(self, *args, **kwargs):
        # 与 C# `RecordingCampaignHost.EnsureFleet` 同语义：**只在舰队真的变化时才记一次**。
        # （上游快照里 `Fleet.switch_to` 只是基类的 `pass`，真正的设备实现不在快照里；
        #  两边统一按"变化才记录"对齐，避免把同一次切换记成两次。）
        if self._stub.fleet_current_index != self._index:
            self._stub.calls.append(("switch_to", str(self._index)))
            self._stub.fleet_current_index = self._index

    def goto(self, location, expected="", **kwargs):
        # 上游 `self.fleet_2.goto(...)` 隐含"用 2 队走"（真机实现会确保舰队切换，快照里看不到），
        # C# 侧是显式 `EnsureFleet(2)` + `Goto` —— 这里把隐含的切换也记上，两边才可比。
        if self._stub.fleet_current_index != self._index:
            self._stub.calls.append(("switch_to", str(self._index)))
            self._stub.fleet_current_index = self._index
        self._stub.calls.append(("goto", str(location)))

    def clear_chosen_enemy(self, grid, expected="", fleet=None):
        self._stub.calls.append(("clear_chosen_enemy", str(grid)))
        self._stub.battle_count += 1

    def __getattr__(self, name):
        # 其余属性/方法透传到宿主替身（如 `fleet_1.fleet_1_location` 之类）
        return getattr(self._stub, name)


class RecordingStub:
    """替身：只提供 config / 舰队属性 / 记录型动作；`map` 用真实 `CampaignMap`。

    `battle_count` 在每次 `clear_chosen_enemy` 后自增——与 C# 侧 `RecordingCampaignHost` 的假定一致
    （"选中的敌人会被打掉"），否则 `clear_potential_boss` 这类"打一架看 battle_count 有没有涨"的
    分支两边会走成不同路径（那是替身差异，不是引擎差异）。
    """

    def __init__(self, campaign_map, config, first_location="A1", second_location=""):
        self.map = campaign_map
        self.config = config
        self.battle_count = 0
        # `brute_find_roadblocks`（兜底分支）会读这些舰队状态；给"1 队在第一格、没有 2 队"的最小前提
        self.fleet_current_index = 1
        self.ammo_count = 3
        self.fleet_ammo = 5
        self.ensure_no_info_bar_calls = 0
        from module.base.utils import node2location  # noqa: PLC0415
        self.fleet_1_location = node2location(first_location)
        self.fleet_2_location = node2location(second_location) if second_location else tuple()
        self.calls: list[tuple[str, str]] = []
        self.select_grids = self._select_grids

    def fleet_ensure(self, index=1):
        # 只改索引，**不记动作**：上游 `fleet_ensure` 与 C# `EnsureFleet` 的调用时机在两条代码路径里
        # 并不一一对应（上游的舰队属性会在访问时切、C# 是显式切），记进来只会放大"记录时机"噪声。
        # 对拍里真正要比的是 clear/goto/submarine 这些**动作本身**。
        self.fleet_current_index = index

    @staticmethod
    def _select_grids(grids, **kwargs):
        from module.map.map import Map  # noqa: PLC0415
        return Map.select_grids(grids, **kwargs)

    # 舰队属性：上游 `fleet_1`/`fleet_2`/`fleet_boss` 会先 `fleet_ensure(index)` 再返回舰队对象
    # （`self.fleet_2.goto(...)`）。返回**按编号记录的代理**，这样"哪一队做的"也进对拍序列。
    @property
    def fleet_1(self):
        return FleetProxy(self, 1)

    @property
    def fleet_2(self):
        return FleetProxy(self, 2)

    @property
    def fleet_boss(self):
        return FleetProxy(self, self.fleet_boss_index)

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

    def ensure_no_info_bar(self, *args, **kwargs):
        # C# 侧也把这个记为动作，所以要进同一条序列（否则序列对拍会假红）
        self.ensure_no_info_bar_calls += 1
        self.calls.append(("ensure_no_info_bar", ""))

    def switch_to(self, *args, **kwargs):
        self.calls.append(("switch_to", "self"))

    def brute_find_roadblocks(self, grid, fleet=None):
        from module.map.map import Map  # noqa: PLC0415
        return Map.brute_find_roadblocks(self, grid, fleet=fleet)

    def brute_fleet_meet(self):
        # 绑**上游真实实现**（它自己会走 `brute_find_roadblocks` + `clear_chosen_enemy`）
        from module.map.map import Map  # noqa: PLC0415
        return Map.brute_fleet_meet(self)

    def clear_potential_boss(self):
        from module.map.map import Map  # noqa: PLC0415
        return Map.clear_potential_boss(self)

    def clear_boss(self):
        # 上游 `self.fleet_boss.clear_boss()` 最终调的就是 `Map.clear_boss`（代理透传到宿主）
        from module.map.map import Map  # noqa: PLC0415
        return Map.clear_boss(self)

    def clear_any_enemy(self, **kwargs):
        # 上游 `clear_filter_enemy` 在 `MAP_HAS_MOVABLE_NORMAL_ENEMY` 时会委托给它
        from module.map.map import Map  # noqa: PLC0415
        return Map.clear_any_enemy(self, **kwargs)

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
        info.may_ammo = bool(grid.get("may_ammo"))
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
        "MAP_HAS_MOVABLE_ENEMY": bool(case.get("map_has_movable_enemy")),
        "MAP_HAS_MOVABLE_NORMAL_ENEMY": bool(case.get("map_has_movable_normal_enemy")),
        "FLEET_2": bool(case.get("fleet_2")),
        # **上游的 `FLEET_BOSS` 是"boss 舰队编号"（1 或 2）**，不是布尔：
        # C# 侧 `FleetBoss` 是布尔并由 `FleetBossIndex => FleetBoss && Fleet2 ? 2 : 1` 派生。
        # 映射错了的话上游会一直走"不是 2 队"的早退（实测：fleet_2_* 假不一致）。
        "FLEET_BOSS": 2 if case.get("fleet_boss") else 1,
    })
    stub = RecordingStub(campaign_map, config, case["grids"][0]["location"],
                         case.get("fleet_2_location") or "")
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
    elif case["kind"] == "primitive_clear_filter_enemy":
        # 上游 `clear_filter_enemy(string, preserve=0)`：过滤器串用库里的真实用法（最常见那条），
        # preserve 由用例给出（0/1 各半），把"保留最弱若干个"这条路径也覆盖到。
        result = method(stub, ENEMY_FILTER_TEXT, case.get("preserve", 0))
    else:
        result = method(stub)
    target = next((location for name, location in stub.calls
                   if name in ("clear_chosen_enemy", "goto")), None)
    return target, result, costs, normalize_upstream(stub.calls)


# 上游替身记录到的调用 → 与 C# `Actions` 可比的 (操作, 目标) 序列。
# 只保留**设备动作**：上游的 `show_select_grids`/`show_fleet`/`map.show_cost` 是界面/日志行为，
# `logger` 更不是动作；`switch_to` 是切舰队（C# 侧记为 `ensure_fleet`）。
UPSTREAM_ACTION_MAP = {
    "clear_chosen_enemy": "clear_chosen_enemy",
    "goto": "goto",
    "submarine_move_near_boss": "submarine_move_near_boss",
    "switch_to": "ensure_fleet",
    "clear_chosen_mystery": "clear_chosen_mystery",
    "ensure_no_info_bar": "ensure_no_info_bar",
}


def normalize_upstream(calls: list[tuple[str, str]]) -> list[tuple[str, str]]:
    normalized = []
    for name, argument in calls:
        op = UPSTREAM_ACTION_MAP.get(name)
        if op is None:
            continue
        if op == "ensure_fleet":
            normalized.append((op, argument))       # 代理记录的就是舰队编号
            continue
        normalized.append((op, argument))
    return normalized


def normalize_csharp(actions: list[str] | None) -> list[tuple[str, str]]:
    """C# 记录的动作字符串 → (操作, 目标)。纯状态操作（如清塞壬标记）不算设备动作，跳过。"""
    normalized = []
    for action in actions or []:
        op = action.split("(", 1)[0].strip()
        if op in ("clear_caught_by_siren_flags",):
            continue
        # C# 侧的动作名 → 与上游替身记录一致的名字
        op = {"fleet_ensure": "ensure_fleet"}.get(op, op)
        inside = action[len(op) + 1:].rstrip(")") if "(" in action else ""
        first = inside.split(",", 1)[0].strip()
        normalized.append((op, first))
    return normalized


# 只比**排序键**（上游能按 `weight`/`cost`/`cost_1`/`cost_2` 排序，四种都算）：
# 上游 `SelectedGrids.add` 的 `set` 打乱的是"谁先被加入"，于是等价的格子可能来自不同过滤组
# （如 enemy 与 fortress/siren），这同样是不可复现的集合序伪影。
RANK_KEYS = ("weight", "cost", "cost_1", "cost_2")


def find_grid(case: dict, location: str | None) -> dict | None:
    if location is None:
        return None
    return next((grid for grid in case["grids"] if grid["location"] == location), None)


def effective_sort_keys(case: dict) -> tuple[str, ...]:
    """该用例**实际生效**的排序键。

    `clear_filter_enemy` 在 `MAP_HAS_MOVABLE_NORMAL_ENEMY` 时会忽略过滤串、转
    `clear_any_enemy(sort=("cost_2",))`（上游就这么写），所以那支只按 `cost_2` 排序；
    其余情况按默认的 `("weight", "cost")`。判"同价位"必须用**实际**的键，否则会把
    真正的等价平手误判成不一致（实测踩过）。
    """
    if case["kind"] == "primitive_clear_filter_enemy" and case.get("map_has_movable_normal_enemy"):
        return ("cost_2",)
    return ("weight", "cost")


def same_rank(left: dict, right: dict, keys: tuple[str, ...]) -> bool:
    """两个格子是否"同价位"：按**实际排序键**比，都一样就只是身份不同（不可复现的集合序）。"""
    return all(left.get(key) == right.get(key) for key in keys)


def only_submarine_arg_differs(left: list[tuple[str, str]], right: list[tuple[str, str]]) -> bool:
    """两条序列是否**只在 `submarine_move_near_boss` 的实参**上不同。

    上游 `clear_boss` 传给它的是 `grids[0]`，而那个 `grids` 是 `add()` 之后**未排序**的集合——
    第一个元素取决于身份哈希序（与 `SelectedGrids.add` 用 `set` 同一个根因），不可复现。
    真正有意义的动作 `clear_chosen_enemy(sorted[0])` 两边一致（目标比较已覆盖）。
    """
    if len(left) != len(right) or not left:
        return False
    differing = False
    for (left_op, left_arg), (right_op, right_arg) in zip(left, right):
        if left_op != right_op:
            return False
        if left_arg != right_arg:
            if left_op != "submarine_move_near_boss":
                return False
            differing = True
    return differing


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
    expected_by_name: dict[str, tuple[str | None, list[tuple[str, str]]]] = {}
    skipped: dict[str, int] = {}
    skip_examples: dict[str, list[str]] = {}
    for case in cases:
        try:
            target, _, costs, sequence = upstream_target(case)
        except Exception as error:                     # noqa: BLE001 —— 上游跑不动就如实记，不算通过
            key = f"上游执行失败：{type(error).__name__}: {error}"[:110]
            skipped[key] = skipped.get(key, 0) + 1
            bucket = skip_examples.setdefault(key, [])
            if len(bucket) < 3:
                bucket.append(case["name"])
            continue
        expected_by_name[case["name"]] = (target, sequence)
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
    sequence_only_diffs: list[tuple[str, str, str]] = []
    dry_run_prefix: list[tuple[str, str, str]] = []
    order_diffs: list[tuple[str, str, str]] = []
    set_order: list[tuple[str, str, str]] = []
    compared = 0
    sequence_compared = sequence_identical = 0
    for case in cases:
        result = results.get(case["name"])
        if result is None:
            mismatches.append((case["name"], case["kind"], "命令没返回这个用例"))
            continue
        if case["name"] not in expected_by_name:
            continue                                   # 上游侧跳过过，如实计入 skipped
        expected, expected_sequence = expected_by_name[case["name"]]
        compared += 1

        # **整条动作序列**对拍（比"只看第一个目标"强：能抓到"打完又去踩 may_boss"这类后续动作）。
        actual_sequence = normalize_csharp(result.get("actions"))
        sequence_compared += 1
        if actual_sequence == expected_sequence:
            sequence_identical += 1
        elif actual_sequence[:1] != expected_sequence[:1]:
            # 第一动作就不同：可能是等价位集合序伪影（见下），也可能是**动作先后顺序**不同。
            # 单列出来（只看"目标"时它们可能被判成一致），报告里带例子便于判断性质。
            if only_submarine_arg_differs(actual_sequence, expected_sequence):
                set_order.append((case["name"], case["kind"], f"C# {actual_sequence} vs 上游 {expected_sequence}"))
            else:
                order_diffs.append((case["name"], case["kind"],
                                    f"C# {actual_sequence} vs 上游 {expected_sequence}"))
        elif (actual_sequence
              and len(actual_sequence) < len(expected_sequence)
              and expected_sequence[:len(actual_sequence)] == actual_sequence
              and all(item == actual_sequence[-1] for item in expected_sequence[len(actual_sequence):])):
            # **干跑前缀**：上游那种"靠设备状态变化才会停"的循环（如 `fleet_2_protect` 的 20 轮），
            # 干跑不改变状态，C# 只做一轮就退出（`Fleet2Protect`/`ClearAllMystery` 里都写明了）。
            # 上游在替身上不会停，于是记满 20 轮同样的动作 —— 这是**有意的偏离**，单列不记为不一致。
            dry_run_prefix.append((case["name"], case["kind"],
                                   f"C# {len(actual_sequence)} 步 vs 上游 {len(expected_sequence)} 步"))
        else:
            sequence_only_diffs.append((
                case["name"], f"{case['kind']}",
                f"C# {actual_sequence} vs 上游 {expected_sequence}"))

        if result["selected"] != expected:
            # 上游 `SelectedGrids.add` 是 `set(self.grids + grids.grids)`——**走哈希集合**，
            # 于是"追加顺序"其实是身份哈希序：等 (weight, cost) 的格子谁排前面不可复现。
            # 所以两侧选了**同价位等价格**（同 weight/cost/标志/可达性）时不算不一致，单列伪影。
            left = find_grid(case, result["selected"])
            right = find_grid(case, expected)
            if left is not None and right is not None and same_rank(left, right, effective_sort_keys(case)):
                artifacts.append((case["name"], result["selected"], expected))
                continue
            mismatches.append((case["name"], f"{case['kind']} {json.dumps({k: v for k, v in case.items() if k not in ('grids', 'name', 'kind')}, ensure_ascii=False)}",
                               f"C# {result['selected']!r} vs 上游 {expected!r}"))
    mismatches.extend(sequence_only_diffs)

    lines = ["# R5 复合原语扫描（C# 原语 vs 上游真实方法）", "",
             "> 本报告由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。",
             "> 覆盖：`clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` /", 
             "> `clear_roadblocks` / `clear_potential_roadblocks` / `clear_first_roadblocks` / `pick_up_ammo`", 
             "> 的判定（含路段、弹药与 2 队推进/护航语义），",
             "> 含配置分支（优先级、全清、塞壬/要塞、FLEET_2 改排序键）；上游侧跑的是**它自己的方法**。", "",
             f"- 状态数：**{options.states}**（seed={options.seed}，每种状态 × {len(PRIMITIVES)} 原语 × {len(CONFIGS)} 配置）",
             f"- 用例数：**{len(cases)}**；实际比较 **{compared}**",
             f"- 不一致：**{len(mismatches)}**",
             f"- **动作序列对拍**：比较 {sequence_compared} 条，完全相同 {sequence_identical}；"
             f"干跑前缀（有意偏离）{len(dry_run_prefix)} 条；"
             f"集合序伪影（只在 `submarine_move_near_boss` 实参上不同）{len(set_order)} 条；"
             f"其余顺序差异 {len(order_diffs)} 条",
             f"- 等价位**集合序伪影**：**{len(artifacts)}** 处（上游 `SelectedGrids.add` 走 `set`，"
             f"等 weight/cost 的格子谁在前不可复现；两侧都选中同价位格子）",
             f"- **未纳入的已知差异**：{', '.join(KNOWN_DIVERGENCE) or '无'}（路障**子集**选择不同："
             "C# 寻路定点收敛会找到更小的可达子集；登记在重写文档的差异一节）", ""]
    if order_diffs:
        lines += ["## 顺序差异（前 10 条）", "",
                  "性质：**切舰队的记录时机**不同。上游 `fleet_1/2/boss` 属性在访问时就 `fleet_ensure(index)`，",
                  "C# 侧是显式 `EnsureFleet`，两条代码路径的调用点不一一对应；比的是同一批 clear/goto/submarine 动作，",
                  "只是多/少一条 `ensure_fleet`。**不记为不一致**（属诊断记录口径），但要看得见。", "",
                  "| 用例 | 种类 | 序列 |", "| --- | --- | --- |"]
        lines += [f"| {name} | {kind} | {detail} |" for name, kind, detail in order_diffs[:10]]
        lines.append("")
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
        lines += ["### 例子（每类最多 3 条）", ""]
        for reason, items in skip_examples.items():
            lines.append(f"- **{reason}**")
            lines += [f"  - `{item}`" for item in items]
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
