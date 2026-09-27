#!/usr/bin/env python3
"""Compare clearing control flow with native Map methods; no device or victory evidence.

Only the terminal action is replaced. Native grid selection, road selection,
filtering and composite returns execute unchanged. A separate channel-contract
case rejects null instead of pretending it is a valid native boolean result.
"""
from __future__ import annotations

import contextlib
import io
import itertools
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from types import SimpleNamespace
from xml.sax.saxutils import escape

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]

HARNESS = r'''
using Alas.Campaign;
using System.Text.Json;
using System.Text.Json.Nodes;

var cases = JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray();
var results = new JsonArray();
foreach (var item in cases) {
    var plan = item!["plan"]!.Deserialize<CampaignPlan>()!;
    var grids = item["grids"]!.Deserialize<CampaignGrid[]>()!;
    var config = item["config"]!.Deserialize<CampaignRuntimeConfig>()!;
    var channel = new ProbeChannel(grids, item["mode"]!.GetValue<string>());
    var host = new DeviceCampaignHost(channel, grids, config);
    bool? value = null;
    bool completed = false;
    string? signal = null, error = null;
    try {
        var result = CampaignHookRunner.Run(plan, plan.Header.Battles[0], host);
        value = result.ReturnValue;
        // The public runner represents CampaignEnd in host state, and other
        // control-flow exceptions in Signal. Compare their native meaning.
        completed = result.Completed && !host.EndRequested;
        signal = host.EndRequested ? "CampaignEnd" : result.Signal;
        if (result.BlockedReason != null && signal == null) error = "refused";
    } catch (InvalidDataException) { error = "invalid_result"; }
      catch (InvalidOperationException) { error = "action_error"; }
    results.Add(new JsonObject {
        ["name"] = item["name"]!.DeepClone(), ["return"] = value,
        ["completed"] = completed, ["signal"] = signal, ["error"] = error,
        ["actions"] = channel.Actions,
    });
}
File.WriteAllText(args[1], results.ToJsonString());

sealed class ProbeChannel(CampaignGrid[] grids, string mode) : ICampaignCallChannel {
    public JsonArray Actions { get; } = [];
    public IReadOnlyList<CampaignGrid> ReadGrids() => grids;
    public JsonNode? Read(string name) => name switch {
        "fleet_current_index" => JsonValue.Create(1),
        "battle_count" => JsonValue.Create(0),
        _ => throw new InvalidOperationException($"Unexpected read: {name}"),
    };
    public void Set(string name, JsonNode? value) => throw new InvalidOperationException($"Unexpected write: {name}");
    public JsonNode? Call(string name, IReadOnlyList<JsonNode?> args,
                         IReadOnlyList<KeyValuePair<string, JsonNode?>> kwargs) {
        if (name == "logger.info") return null;
        string expected = kwargs.FirstOrDefault(k => k.Key == "expected").Value?.GetValue<string>() ?? "";
        string grid = args[0]!.GetValue<string>().TrimStart('#');
        Actions.Add(new JsonArray(name, grid, expected));
        if (name == "goto") return null;
        if (name != "clear_chosen_enemy") throw new InvalidOperationException($"Unexpected action: {name}");
        return mode switch {
            "true" => JsonValue.Create(true), "false" => JsonValue.Create(false), "null" => null,
            "MapEnemyMoved" or "CampaignEnd" => throw new CampaignControlFlowSignal(mode, "fixture"),
            "refused" => throw new NotSupportedException("fixture refusal"),
            _ => throw new InvalidOperationException("fixture action error"),
        };
    }
}
'''


def build_cases():
    sys.path.insert(0, str(Path(os.environ.get('ALAS_REPO') or ROOT / '.runtime/engine')))
    from module.base.utils import location2node
    from module.campaign.campaign_base import CampaignBase
    from module.exception import CampaignEnd, MapEnemyMoved
    from module.map.map import Map
    from module.map.map_base import CampaignMap
    from module.map.map_grids import RoadGrids

    class Probe(Map):
        battle_default = CampaignBase.battle_default

        def __init__(self, grids, config, mode):
            self.map = CampaignMap('return-fixture')
            self.map.shape = 'B1'
            for fields in grids:
                grid = self.map[(ord(fields['Location'][0]) - ord('A'), 0)]
                for key, value in fields.items():
                    if key == 'Location':
                        continue
                    # Fixture field spelling only; no copied native selection rules.
                    attribute = re.sub(r'(?<!^)(?=[A-Z0-9])', '_', key).lower()
                    setattr(grid, attribute, value)
            self.config = SimpleNamespace(
                EnemyPriority_EnemyScaleBalanceWeight='default_mode', MAP_CLEAR_ALL_THIS_TIME=False,
                MAP_HAS_SIREN=config['MapHasSiren'], MAP_HAS_FORTRESS=config['MapHasFortress'],
                MAP_HAS_MOVABLE_NORMAL_ENEMY=config['MapHasMovableNormalEnemy'], FLEET_2=False)
            self.fleet_current_index = 1
            self.mode = mode
            self.actions = []

        def clear_chosen_enemy(self, grid, expected=''):
            self.actions.append(['clear_chosen_enemy', location2node(grid.location), expected])
            if self.mode in ('MapEnemyMoved', 'CampaignEnd'):
                raise {'MapEnemyMoved': MapEnemyMoved, 'CampaignEnd': CampaignEnd}[self.mode]('fixture')
            if self.mode in ('action_error', 'refused'):
                raise RuntimeError(self.mode)
            return self.mode == 'true'

        def goto(self, grid):
            self.actions.append(['goto', location2node(grid.location), ''])

    variants = [
        ('clear_enemy', 'enemy'), ('clear_any_enemy', 'enemy'),
        ('clear_any_enemy', 'siren'), ('clear_any_enemy', 'fortress'),
        ('clear_siren', 'siren'), ('clear_siren', 'fortress'),
        ('clear_roadblocks', 'enemy'), ('clear_potential_roadblocks', 'enemy'),
        ('clear_first_roadblocks', 'enemy'), ('clear_filter_enemy', 'enemy'),
        ('clear_filter_enemy', 'movable'), ('battle_default', 'enemy'),
        # These two must continue forwarding False, unlike the selection wrappers.
        ('clear_chosen_enemy', 'direct'), ('brute_clear_boss', 'caught'),
    ]
    cases = []
    for (op, variant), available, mode, shape in itertools.product(
            variants, (True, False),
            ('false', 'true', 'MapEnemyMoved', 'CampaignEnd', 'action_error', 'refused'),
            ('terminal', 'branch')):
        if not available and variant in ('direct', 'caught'):
            continue
        grids = [{'Location': 'A1', 'IsEnemy': available and variant in ('enemy', 'movable', 'direct'),
                  'IsSiren': available and variant == 'siren',
                  'IsFortress': available and variant == 'fortress',
                  'MayBoss': variant == 'caught', 'IsCaughtBySiren': variant == 'caught',
                  'EnemyScale': 1, 'EnemyGenre': 'Light', 'Weight': 10, 'Cost': 0, 'Cost2': 0},
                 {'Location': 'B1', 'Weight': 10, 'Cost': 1, 'Cost2': 1}]
        config = dict(MapHasSiren=True, MapHasFortress=True, MapHasMovableNormalEnemy=variant == 'movable')
        probe = Probe(grids, config, mode)
        a1, b1 = probe.map[(0, 0)], probe.map[(1, 0)]
        native_args, positional = [], []
        if 'roadblocks' in op:
            block = [a1, b1] if op == 'clear_potential_roadblocks' else [a1]
            native_args = [[RoadGrids([block])]]
            positional = [{'__roads__': [[[list(g.location) for g in block]]]}]
        elif op == 'clear_filter_enemy':
            native_args = positional = ['1L', 0]
        elif op == 'clear_chosen_enemy':
            native_args, positional = [a1], [{'__grid__': [0, 0]}]
        expected = {'return': None, 'completed': False, 'signal': None, 'error': None}
        try:
            value = getattr(probe, op)(*native_args)
            if shape == 'branch' and not value:
                probe.goto(b1)
            expected.update({'return': value, 'completed': True})
        except (MapEnemyMoved, CampaignEnd) as exc:
            expected['signal'] = type(exc).__name__
        except RuntimeError as exc:
            expected['error'] = str(exc)
        call = {'op': op, 'args': {'positional': positional}}
        steps = [dict(call, kind='terminal')] if shape == 'terminal' else [
            {'kind': 'branch', 'test': {'call': call}, 'body': [{'kind': 'return', 'value': True}],
             'orelse': [{'kind': 'call', 'op': 'goto', 'args': {'positional': [{'__grid__': [1, 0]}]}},
                       {'kind': 'return', 'value': False}]}]
        cases.append({'name': f'{op}-{variant}-{available}-{mode}-{shape}', 'mode': mode,
                      'grids': grids, 'config': config,
                      'plan': {'campaign': {'battles': [{'method': 'battle_0', 'plan_complete': True,
                                                        'steps': steps}]}},
                      'expect': dict(expected, actions=probe.actions)})
    # None is outside ICampaignPrimitiveHost's boolean action contract. Keep its
    # explicit rejection separate from the upstream boolean/control-flow oracle.
    invalid = json.loads(json.dumps(cases[0]))
    invalid.update(name='invalid-null-action-result', mode='null')
    invalid['expect'].update({'return': None, 'completed': False, 'error': 'invalid_result'})
    cases.append(invalid)
    return cases


def main():
    with contextlib.redirect_stdout(io.StringIO()):
        cases = build_cases()
    runtime = ROOT / '.runtime/verification'
    runtime.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='clear-returns-', dir=runtime) as folder:
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
            print('FAIL: C# clear-return harness exited', result.returncode)
            print((result.stdout + result.stderr).replace(str(ROOT), '<repo>')[-3000:])
            return 1
        actual = json.loads((work / 'actual.json').read_text(encoding='utf-8'))
    expected = {case['name']: case['expect'] for case in cases}
    failures, seen = [], set()
    for row in actual:
        name = row.pop('name')
        if name in seen or name not in expected:
            failures.append(f'duplicate/unknown result: {name}')
        seen.add(name)
        if row != expected.get(name):
            failures.append(f'{name}: actual={row}, upstream/contract={expected.get(name)}')
    if seen != set(expected):
        failures.append('missing case results')
    if failures:
        print(f'FAIL: {len(failures)} / {len(cases)} clear-return cases')
        print('\n'.join(failures[:12]))
        return 1
    print(f'PASS: {len(cases) - 1} native clearing control-flow cases + 1 null channel rejection; no device actions')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
