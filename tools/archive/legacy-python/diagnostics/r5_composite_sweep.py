#!/usr/bin/env python3
"""Strict composite primitive comparison with native Map/Fleet methods, without devices.

Native properties and method dispatch remain intact. Action endpoints record
arguments and apply the same explicit counter feedback as RecordingCampaignHost.
Results include returns, full action sequences and post-call state. Every unknown,
missing result, native failure or mismatch makes the scan fail; there are no
prefix, order, equal-rank or submarine-target exemptions.
"""
from __future__ import annotations

import argparse
from collections import Counter
import contextlib
import io
import json
import os
from pathlib import Path
import random
import re
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
from xml.sax.saxutils import escape

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]
REPORT = ROOT / 'docs/archive/reports/r5-composite-sweep.md'
WORK = ROOT / '.runtime/verification/composite-sweep'
UPSTREAM = Path(os.environ.get('ALAS_REPO') or ROOT / '.runtime/engine')

GENRES = ["Light", "Main", "Carrier", "Treasure"]
# 库里的真实过滤器串（`clear_filter_enemy` 用得最多的那条）
ENEMY_FILTER_TEXT = "1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C"
PRIMITIVES = ["clear_enemy", "clear_any_enemy", "clear_siren", "clear_boss",
              "clear_roadblocks", "clear_potential_roadblocks", "clear_first_roadblocks",
              "pick_up_ammo", "fleet_2_push_forward", "fleet_2_protect", "brute_clear_boss",
              "brute_fleet_meet", "clear_potential_boss", "clear_filter_enemy",
              "pick_up_flare", "check_accessibility", "map_select"]

ROADBLOCK_PRIMITIVES = {"clear_roadblocks", "clear_potential_roadblocks", "clear_first_roadblocks"}
# 收**一个格子参数**的原语（`pick_up_flare(grid)` / `fleet_2_rescue(grid)`）：对拍时两侧用同一个目标格
GRID_ARGUMENT_PRIMITIVES = {"pick_up_flare", "fleet_2_rescue", "check_accessibility"}
# `map.select(**flags)` 的返回值是**格子集合**，没有设备动作：对拍时比整份集合，而不是"第一个动作"
MAP_SELECT_PRIMITIVES = {"map_select"}
MAP_SELECT_FLAGS = [
    {"is_enemy": True},
    {"is_boss": True},
    {"may_boss": True},
    {"is_siren": True},
    {"is_ammo": True},
    {"is_enemy": False},
    {"is_land": True},
    {"is_cleared": True},
    {"is_fleet": True},
    {"may_ambush": True},
    {"is_land": True, "is_cleared": True},
    {"is_boss": True, "is_siren": True},
]

# **默认不跑的原语**：`fleet_2_rescue` 会走 `brute_find_roadblocks`，而上游那套枚举**没有上限**
# （`itertools.product` 按敌人数指数增长），在敌人多的随机状态上能把扫描拖到分钟级甚至更久。
# 它本身是有覆盖的（`verify_r5_execution` 的夹具用例），需要全量跑时用 `--include-slow`。
SLOW_PRIMITIVES = ["fleet_2_rescue"]
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


def build_cases(states: int, seed: int, primitives: list[str] | None = None) -> list[dict]:
    primitives = primitives or PRIMITIVES
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
                    # 上游地图数据全是 `--`：`decode()` 后 may_* 全假、`may_ambush = not(...) = 真`。
                    # 夹具必须镜像这个推导结果，否则 `map.select(may_ambush=True)` 这类对拍会假红（实测踩过）。
                    "may_ambush": True,
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
        for kind in primitives:
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
                if kind in MAP_SELECT_PRIMITIVES:
                    # 每种配置换一组筛选标志（覆盖 True/False、单键/多键）
                    case["flags"] = MAP_SELECT_FLAGS[config_index % len(MAP_SELECT_FLAGS)]
                if kind == "check_accessibility":
                    case["target"] = max(grids, key=lambda item: (item["weight"], item["location"]))["location"]
                    case["fleet"] = ["", "1", "2", "boss"][config_index % 4]
                if kind in GRID_ARGUMENT_PRIMITIVES:
                    # 目标格取"该状态里 weight 最大的格子"：够具体、又随状态变化，避免每次都打同一格
                    target = max(grids, key=lambda item: (item["weight"], item["location"]))
                    case["target"] = target["location"]
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


HARNESS = r'''
using Alas.Campaign;
using System.Text.Json;
using System.Text.Json.Nodes;

var rows = new JsonArray();
foreach (var item in JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray()) {
    var host = new RecordingCampaignHost(item!["execution_grids"]!.Deserialize<CampaignGrid[]>()!,
        item["execution_config"]!.Deserialize<CampaignRuntimeConfig>()!) {
        FleetCurrentIndex = item["fleet_current_index"]!.GetValue<int>(),
        Fleet1Location = item["fleet_1_location"]!.GetValue<string>(),
        Fleet2Location = item["fleet_2_location"]!.GetValue<string>(),
    };
    object? value = null;
    string? error = null;
    try {
        var step = item["step"]!.Deserialize<CampaignPlanStep>()!;
        // map.select is an evaluator intrinsic, outside the primitive registry.
        if (step.Op == "map.select") value = CampaignPrimitives.MapSelect(host, step);
        else {
            if (!CampaignPrimitiveRegistry.TryGet(step.Op, out var primitive))
                throw new NotSupportedException($"Unregistered primitive: {step.Op}");
            value = primitive.Execute(host, step);
        }
        if (value is CampaignGridSet selected)
            value = selected.Grids.Select(g => g.Location).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    } catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
    rows.Add(new JsonObject {
        ["name"] = item["name"]!.DeepClone(), ["value"] = JsonSerializer.SerializeToNode(value),
        ["error"] = error, ["actions"] = JsonSerializer.SerializeToNode(host.Actions),
        ["state"] = new JsonObject {
            ["fleet_current_index"] = host.FleetCurrentIndex, ["battle_count"] = host.BattleCount,
            ["ammo_count"] = host.AmmoCount, ["fleet_ammo"] = host.FleetAmmo,
            ["picked_flare"] = JsonSerializer.SerializeToNode(host.PickedFlare.OrderBy(x => x).ToArray()),
            ["grids"] = JsonSerializer.SerializeToNode(host.Grids.ToDictionary(g => g.Location, g => new {
                is_flare=g.IsFlare, is_caught_by_siren=g.IsCaughtBySiren, is_enemy=g.IsEnemy,
                is_fleet=g.IsFleet, cost=g.Cost, cost_1=g.Cost1, cost_2=g.Cost2,
            })),
        },
    });
}
File.WriteAllText(args[1], rows.ToJsonString());
'''

STATE_GRID_FIELDS = ('is_flare', 'is_caught_by_siren', 'is_enemy', 'is_fleet', 'cost', 'cost_1', 'cost_2')


def native_probe(case):
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))
    from module.base.utils import node2location, location2node
    from module.map.map import Map
    from module.map.map_base import CampaignMap

    class Probe(Map):
        def __init__(self):
            self.map = CampaignMap('composite-fixture')
            locations = [node2location(g['location']) for g in case['grids']]
            width, height = max(x for x, _ in locations) + 1, max(y for _, y in locations) + 1
            self.map.shape = location2node((width - 1, height - 1))
            self.map.map_data = '\n'.join(' '.join('--' for _ in range(width)) for _ in range(height))
            self.map.load_map_data()
            self.map.grid_connection_initial(wall=False)
            for fields in case['grids']:
                grid = self.map[node2location(fields['location'])]
                for key, value in fields.items():
                    if key != 'location':
                        setattr(grid, key, value)
            self.config = SimpleNamespace(
                EnemyPriority_EnemyScaleBalanceWeight=case.get('enemy_priority') or '',
                MAP_CLEAR_ALL_THIS_TIME=case.get('map_clear_all_this_time', False),
                MAP_HAS_SIREN=case.get('map_has_siren', False),
                MAP_HAS_FORTRESS=case.get('map_has_fortress', False),
                MAP_HAS_MOVABLE_ENEMY=case.get('map_has_movable_enemy', False),
                MAP_HAS_MOVABLE_NORMAL_ENEMY=case.get('map_has_movable_normal_enemy', False),
                MAP_HAS_AMBUSH=False, FLEET_2=case.get('fleet_2', False),
                FLEET_BOSS=2 if case.get('fleet_boss') else 1)
            self.fleet_1_location = node2location(case['fleet_1_location'])
            self.fleet_2_location = node2location(case['fleet_2_location']) if case['fleet_2_location'] else ()
            self.fleet_current_index = case['fleet_current_index']
            self.battle_count, self.ammo_count, self.fleet_ammo = 0, 3, 5
            self.picked_flare, self.picked_light_house, self.calls = [], [], []
            self.deadline = time.monotonic() + 3

        def find_path_initial(self):
            # Native brute-force search can grow without bound on synthetic
            # states. Exceeding the diagnostic budget is unverified, never PASS.
            if time.monotonic() > self.deadline:
                raise TimeoutError('native path search exceeded 3s diagnostic budget')
            return super().find_path_initial()

        def fleet_ensure(self, index):
            changed = self.fleet_current_index != index
            if changed:
                self.calls.append(['fleet_ensure', [str(index)], {}])
                self.fleet_current_index = index
            return changed

        def clear_chosen_enemy(self, grid, expected=''):
            self.calls.append(['clear_chosen_enemy', [location2node(grid.location)], {'expected': expected}])
            self.battle_count += 1
            return True

        def goto(self, grid, expected=''):
            self.calls.append(['goto', [location2node(grid.location)], {'expected': expected}])

        def submarine_move_near_boss(self, grid):
            self.calls.append(['submarine_move_near_boss', [location2node(grid.location)], {}])
            return True

        def ensure_no_info_bar(self):
            self.calls.append(['ensure_no_info_bar', [], {}])

    return Probe()


def state_of(probe):
    from module.base.utils import location2node
    return dict(fleet_current_index=probe.fleet_current_index, battle_count=probe.battle_count,
                ammo_count=probe.ammo_count, fleet_ammo=probe.fleet_ammo,
                picked_flare=sorted(location2node(g.location) for g in probe.picked_flare),
                grids={location2node(g.location): {key: getattr(g, key) for key in STATE_GRID_FIELDS}
                       for g in probe.map})


def prepare_case(case):
    probe = native_probe(case)
    from module.base.utils import node2location
    probe.find_path_initial()
    # Export the actual initial native costs and flags, before the method mutates
    # them. The C# side receives precisely this snapshot, not post-call costs.
    for fields in case['grids']:
        native = probe.map[node2location(fields['location'])]
        for key in STATE_GRID_FIELDS:
            fields[key] = getattr(native, key)
    case['execution_grids'] = [{''.join(p.title() for p in k.split('_')): v for k, v in g.items()}
                               for g in case['grids']]
    config_names = ('enemy_priority', 'map_clear_all_this_time', 'map_has_siren', 'map_has_fortress',
                    'fleet_2', 'fleet_boss', 'map_has_movable_enemy', 'map_has_movable_normal_enemy')
    case['execution_config'] = {''.join(p.title() for p in k.split('_')): case[k]
                                for k in config_names if k in case}
    op = case['kind'].removeprefix('primitive_')
    positional, kwargs = [], {}
    if op in ROADBLOCK_PRIMITIVES:
        positional = [{'__roads__': [[[list(node2location(cell)) for cell in block] for block in road]
                                     for road in case['roads']]}]
    elif op in GRID_ARGUMENT_PRIMITIVES:
        positional = [{'__grid__': list(node2location(case['target']))}]
        if op == 'check_accessibility':
            if case.get('fleet'):
                kwargs['fleet'] = case['fleet']
    elif op == 'clear_filter_enemy':
        positional = [case['filter'], case['preserve']]
    elif op == 'map_select':
        kwargs = case['flags']
    case['step'] = dict(kind='terminal', op='map.select' if op == 'map_select' else op,
                        args=dict(positional=positional, keyword=kwargs))
    return probe


def upstream_result(case, probe):
    from module.base.utils import node2location, location2node
    from module.map.map_grids import RoadGrids
    op = case['kind'].removeprefix('primitive_')
    error, value = None, None
    try:
        if op == 'map_select':
            value = sorted(location2node(g.location) for g in probe.map.select(**case['flags']))
        elif op in ROADBLOCK_PRIMITIVES:
            roads = [RoadGrids([[probe.map[node2location(cell)] for cell in block] for block in road])
                     for road in case['roads']]
            value = getattr(probe, op)(roads)
        elif op == 'pick_up_flare':
            from campaign.campaign_main.campaign_14_base import CampaignBase
            value = CampaignBase.pick_up_flare(probe, probe.map[node2location(case['target'])])
        elif op in GRID_ARGUMENT_PRIMITIVES:
            args = [probe.map[node2location(case['target'])]]
            if op == 'check_accessibility':
                args.append(case.get('fleet') or None)
            value = getattr(probe, op)(*args)
        elif op == 'clear_filter_enemy':
            value = probe.clear_filter_enemy(case['filter'], case['preserve'])
        else:
            value = getattr(probe, op)()
    except Exception as exc:
        error = type(exc).__name__ + ': ' + str(exc)
    return dict(name=case['name'], value=value, error=error, actions=probe.calls, state=state_of(probe))


def normalize_csharp(actions):
    """Preserve operation, positionals and every keyword; reject unknown formats.

    set_flag is a local write, independently checked in the complete post-state.
    It is not silently dropped as an unverified action.
    """
    known = {'clear_chosen_enemy', 'goto', 'fleet_ensure', 'submarine_move_near_boss', 'ensure_no_info_bar'}
    rows = []
    for action in actions:
        match = re.fullmatch(r'([a-z_0-9]+)\((.*)\)', action)
        if match is None:
            raise ValueError('unrecognized action: ' + action)
        op, arguments = match.groups()
        if op == 'set_flag':
            if not re.fullmatch(r'[A-Z]+[0-9]+,is_flare=(True|False)', arguments):
                raise ValueError('unverified state write: ' + action)
            continue
        if op not in known:
            raise ValueError('unverified action: ' + action)
        positional, keyword = [], {}
        for item in arguments.split(',') if arguments else []:
            item = item.strip()
            if '=' in item:
                key, value = item.split('=', 1)
                if key in keyword:
                    raise ValueError('duplicate action keyword: ' + action)
                keyword[key] = value
            else:
                positional.append(item)
        if op in ('goto', 'clear_chosen_enemy'):
            keyword.setdefault('expected', '')
        rows.append([op, positional, keyword])
    return rows


def compare_result(expected, actual):
    if expected.get('error'):
        return ['native_unverified: ' + expected['error']]
    if actual.get('error'):
        return ['csharp_error: ' + actual['error']]
    differences = []
    try:
        actions = normalize_csharp(actual['actions'])
    except (KeyError, TypeError, ValueError) as exc:
        differences.append('action_encoding: ' + str(exc))
    else:
        if actions != expected['actions']:
            differences.append('actions')
    # JSON/Python considers False == 0. The method contract must preserve types.
    if 'value' not in actual or type(actual['value']) is not type(expected['value']) or actual['value'] != expected['value']:
        differences.append('value')
    if actual.get('state') != expected['state']:
        differences.append('state')
    return differences


def audit_results(expected, actual):
    if not expected:
        raise ValueError('empty native sample cannot prove equivalence')
    expected_by_name = {row['name']: row for row in expected}
    if len(expected_by_name) != len(expected):
        raise ValueError('duplicate native case identity')
    failures, seen = [], set()
    for row in actual:
        name = row.get('name')
        if name in seen or name not in expected_by_name:
            failures.append(dict(name=name, differences=['duplicate_or_unknown_result']))
            continue
        seen.add(name)
        differences = compare_result(expected_by_name[name], row)
        if differences:
            failures.append(dict(name=name, differences=differences))
    for name in expected_by_name.keys() - seen:
        failures.append(dict(name=name, differences=['missing_result']))
    return failures


def run_csharp(cases):
    WORK.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='harness-', dir=WORK) as folder:
        work = Path(folder)
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        (work / 'check.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable></PropertyGroup><ItemGroup>
<ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" />
</ItemGroup></Project>''', encoding='utf-8')
        (work / 'cases.json').write_text(json.dumps(cases), encoding='utf-8')
        result = subprocess.run([str(dotnet_env.executable(ROOT)), 'run', '--project', str(work / 'check.csproj'),
            '-c', 'Release', '--', str(work / 'cases.json'), str(work / 'actual.json')],
            cwd=ROOT, env=dotnet_env.apply(os.environ, ROOT), capture_output=True,
            encoding='utf-8', errors='replace', timeout=180)
        if result.returncode:
            raise RuntimeError('C# harness failed: ' + (result.stdout + result.stderr)[-2000:].replace(str(ROOT), '<repo>'))
        return json.loads((work / 'actual.json').read_text(encoding='utf-8'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--states', type=int, default=20)
    parser.add_argument('--seed', type=int, default=13)
    parser.add_argument('--include-slow', action='store_true')
    options = parser.parse_args()
    if options.states < 1:
        parser.error('--states must be positive')
    primitives = [*PRIMITIVES, *SLOW_PRIMITIVES] if options.include_slow else PRIMITIVES
    cases = build_cases(options.states, options.seed, primitives)
    expected = []
    prepared = []
    for case in cases:
        with contextlib.redirect_stdout(io.StringIO()):
            try:
                probe = prepare_case(case)
            except Exception as exc:
                expected.append(dict(name=case['name'], error='initial_state: ' + type(exc).__name__,
                                     value=None, actions=[], state={}))
                continue
            expected.append(upstream_result(case, probe))
            prepared.append(case)
    actual = run_csharp(prepared)
    failures = audit_results(expected, actual)
    WORK.mkdir(parents=True, exist_ok=True)
    (WORK / 'results.json').write_text(json.dumps(dict(cases=cases, expected=expected, actual=actual,
        failures=failures), ensure_ascii=False, indent=2), encoding='utf-8')
    counts = Counter(part.split(':', 1)[0] for row in failures for part in row['differences'])
    lines = ['# R5 复合原语严格扫描', '', '> 由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。',
             '> 上游使用原生 Map/Fleet 属性及方法；C# 执行注册原语，保留 bool/None 返回。',
             '> 动作只录制，计数反馈为显式替身假设；不证明设备效果、真实通关或完整状态等价。', '',
             f'- 状态数：**{options.states}**（seed={options.seed}）',
             f'- 用例数：**{len(cases)}**（{len(primitives)} 个原语 × {len(CONFIGS)} 种配置）',
             f'- 完整比较通过：**{len(cases) - len(failures)}**',
             f'- 失败或未验证：**{len(failures)}**',
             '- 核对返回值及类型、全部动作及参数、计数/舰队/弹药/拾取列表与地图状态字段。',
             '- 不豁免动作前缀、切队顺序、同权目标或潜艇目标；原生失败、缺结果也令检查失败。',
             '- 原生寻路超过每例 3 秒诊断预算记为未验证，不改变上游结束判据。',
             '- 未运行的慢原语：' + ('无' if options.include_slow else ', '.join(SLOW_PRIMITIVES)), '',
             '| 差异类别（可重叠） | 次数 |', '| --- | --- |',
             *[f'| {key} | {value} |' for key, value in sorted(counts.items())], '',
             '## 失败样本（最多 30 条）', '', '| 用例 | 差异 |', '| --- | --- |',
             *[f"| {row['name']} | {', '.join(row['differences']).replace('|', '/')} |" for row in failures[:30]], '',
             '集合并集的身份哈希顺序可能造成目标差异；此扫描不据此豁免。需以具体候选集合、',
             '状态与原生顺序证据继续定位；当前失败不能宣称为全量语义一致。', '']
    REPORT.write_text('\n'.join(lines), encoding='utf-8')
    print(f'{"FAIL" if failures else "PASS"}: composite scan {len(cases)} cases, {len(failures)} failures/unverified; {dict(counts)}')
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
