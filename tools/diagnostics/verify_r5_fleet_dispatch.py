#!/usr/bin/env python3
"""Compare fleet receiver dispatch with upstream Fleet properties, without devices.

The native properties are executed unchanged; fleet_ensure and goto only record
calls. Both C# host implementations receive the same synthetic plans and state.
This verifies dispatch, arguments and signals, not native campaign completion.
"""
from __future__ import annotations

import itertools
import json
import os
from pathlib import Path
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
    var config = new CampaignRuntimeConfig(Fleet2:item["fleet2"]!.GetValue<bool>(),
        FleetBoss:item["boss2"]!.GetValue<bool>());
    int current = item["current"]!.GetValue<int>();
    string? scenario = item["scenario"]?.GetValue<string>();
    CampaignGrid[] grids = scenario == "caught" ? [new("A1", MayBoss:true, IsCaughtBySiren:true), new("B1")]
        : scenario == "pushed" ? [new("A1", Weight:10), new("B1", Weight:10)]
        : [new("A1", IsBoss:true), new("B1")];
    foreach (bool device in new[] {false, true}) {
        var channel = new RecordingCampaignCallChannel(new Dictionary<string, JsonNode?> {
            ["fleet_current_index"] = JsonValue.Create(current),
            ["battle_count"] = JsonValue.Create(0),
            ["fleet_1_location"] = JsonValue.Create("A1"),
            ["fleet_2_location"] = JsonValue.Create("B1"),
        }).WithGrids(grids);
        var recorder = new RecordingCampaignHost(grids, config) {
            FleetCurrentIndex = current, Fleet1Location = "A1", Fleet2Location = "B1" };
        ICampaignPrimitiveHost host = device ? new DeviceCampaignHost(channel, grids, config) : recorder;
        var result = CampaignHookRunner.Run(plan, plan.Header.Battles[0], host);
        var actions = device ? channel.Calls.Where(c => c.Name != "logger.info").Select(c =>
            c.Name == "fleet_ensure" ? $"fleet_ensure({c.Kwargs["index"]})"
            : $"{c.Name}({string.Join(", ", c.Args.Select(a => a.TrimStart('#')))})").ToArray()
            : recorder.Actions.Select(a => a.Replace(", expected=)", ")")).ToArray();
        results.Add(new JsonObject {
            ["name"] = item["name"]!.DeepClone(), ["host"] = device ? "device" : "recording",
            ["current"] = host.FleetCurrentIndex, ["return"] = result.ReturnValue,
            ["completed"] = result.Completed, ["signal"] = result.Signal,
            ["blocked"] = result.BlockedReason,
            ["static_first_implemented"] = CampaignPlanExecutor.DryRun(plan, plan.Header.Battles[0]).Steps[0].Implemented,
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["invoked_ops"] = device ? null : JsonSerializer.SerializeToNode(recorder.InvokedOps),
        });
    }
}
File.WriteAllText(args[1], results.ToJsonString());
'''


def build_cases():
    upstream = Path(os.environ.get('ALAS_REPO') or ROOT / '.runtime/engine')
    sys.path.insert(0, str(upstream))
    from module.map.fleet import Fleet
    from module.map.map import Map
    from module.map.map_grids import SelectedGrids
    from module.exception import MapEnemyMoved

    class Probe(Fleet):
        def __init__(self, current, fleet2, boss2):
            self.config = SimpleNamespace(FLEET_2=fleet2, FLEET_BOSS=2 if boss2 else 1)
            self.fleet_current_index = current
            self.actions = []

        def fleet_ensure(self, index):
            self.actions.append(f'fleet_ensure({index})')
            self.fleet_current_index = index
            return True

        def goto(self, grid):
            self.actions.append(f'goto({grid})')

        def clear_boss(self, enabled=False, target='A1'):
            self.goto(target)
            return enabled

        def helper(self, enabled=False, target='A1'):
            return self.clear_boss(enabled, target)

        def moved(self):
            raise MapEnemyMoved('fixture')

        def clear_chosen_enemy(self, grid):
            self.actions.append(f'clear_chosen_enemy({grid.name})')
            return True

    def ret(value):
        return {'kind': 'return', 'value': value}

    def branch(test):
        return {'kind': 'branch', 'test': test, 'body': [ret(True)], 'orelse': [ret(False)]}

    cases = []
    for prefix, current, fleet2, boss2 in itertools.product(
            ('fleet_1', 'fleet_2', 'fleet_boss', 'fleet_submarine'), (1, 2), (False, True), (False, True)):
        for mode in ('base', 'override_default', 'override_keyword', 'custom', 'signal', 'incomplete'):
            method = {'base': 'switch_to', 'custom': 'helper', 'signal': 'moved'}.get(mode, 'clear_boss')
            shapes = ('terminal',) if mode in ('base', 'signal', 'incomplete') else (
                'terminal', 'call', 'assign', 'branch', 'expr')
            for shape in shapes:
                probe = Probe(current, fleet2, boss2)
                receiver = getattr(probe, prefix)  # Execute the real upstream property.
                expected = {'current': probe.fleet_current_index, 'return': None,
                            'actions': probe.actions, 'signal': None, 'completed': mode != 'incomplete'}
                supplied = mode in ('override_keyword', 'custom')
                kwargs = {'enabled': True, 'target': 'B1'} if supplied else {}
                if mode != 'incomplete':
                    try:
                        value = getattr(receiver, method)(**kwargs)
                        expected['return'] = None if shape == 'call' else value if shape == 'terminal' else bool(value)
                    except MapEnemyMoved:
                        expected.update(completed=False, signal='MapEnemyMoved')
                op = prefix + '.' + method
                call = {'op': op, 'args': {'keyword': {'enabled': True,
                    'target': {'__grid__': [1, 0]}} if supplied else {}}}
                if shape == 'branch':
                    steps = [branch({'call': call})]
                elif shape == 'expr':
                    steps = [branch({'expr': {'call': call}})]
                elif shape == 'assign':
                    steps = [dict(call, kind='assign', target='answer'), branch({'local': 'answer'})]
                else:
                    steps = [dict(call, kind=shape)]
                hooks = [{'method': 'battle_0', 'steps': steps, 'plan_complete': True}]
                if mode != 'base':
                    hooks.append({'method': method, 'plan_complete': mode != 'incomplete',
                        'parameters': {} if mode == 'signal' else {'enabled': False, 'target': {'__grid__': [0, 0]}},
                        'parameter_order': [] if mode == 'signal' else ['enabled', 'target'],
                        'steps': [{'kind': 'raise', 'signal': 'MapEnemyMoved'}] if mode == 'signal' else [
                            {'kind': 'call', 'op': 'goto', 'args': {'positional': [{'__param__': 'target'}]}},
                            branch({'expr': {'local': 'enabled'}})]})
                cases.append({'name': f'{prefix}-{current}-{fleet2}-{boss2}-{mode}-{shape}',
                    'current': current, 'fleet2': fleet2, 'boss2': boss2,
                    'plan': {'campaign': {'battles': hooks}}, 'expect': expected, 'mode': mode})
    class StateMap:
        def __init__(self, scenario):
            self.grids = [SimpleNamespace(name=name, location=(index, 0), is_boss=False,
                is_land=False, weight=10, cost=index, may_boss=scenario == 'caught' and index == 0,
                is_caught_by_siren=scenario == 'caught' and index == 0)
                for index, name in enumerate(('A1', 'B1'))]

        def select(self, **kwargs):
            return SelectedGrids(self.grids).select(**kwargs)

        def __getitem__(self, location):
            return next(grid for grid in self.grids if grid.location == location)

    # Same property semantics must hold in native-method translations which
    # internally evaluate fleet_1/fleet_2, not only at exported call sites.
    for current, fleet2, boss2, scenario in itertools.product((1, 2), (False, True), (False, True), ('caught', 'pushed')):
        probe = Probe(current, fleet2, boss2)
        probe.map = StateMap(scenario)
        probe.fleet_1_location, probe.fleet_2_location = (0, 0), (1, 0)
        method = 'brute_clear_boss' if scenario == 'caught' else 'fleet_2_push_forward'
        value = getattr(Map, method)(probe)
        cases.append({'name': f'internal-{scenario}-{current}-{fleet2}-{boss2}', 'current': current,
            'fleet2': fleet2, 'boss2': boss2, 'scenario': scenario, 'mode': 'base',
            'plan': {'campaign': {'battles': [{'method': 'battle_0', 'plan_complete': True,
                'steps': [{'kind': 'terminal', 'op': method}]}]}},
            'expect': {'current': probe.fleet_current_index, 'return': value, 'actions': probe.actions,
                'completed': True, 'signal': None}})
    return cases


def main():
    cases = build_cases()
    runtime = ROOT / '.runtime/verification'
    runtime.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='fleet-dispatch-', dir=runtime) as folder:
        work = Path(folder)
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        project = f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>
<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup><ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" />
</ItemGroup></Project>'''
        (work / 'check.csproj').write_text(project, encoding='utf-8')
        (work / 'cases.json').write_text(json.dumps(cases), encoding='utf-8')
        result = subprocess.run([str(dotnet_env.executable(ROOT)), 'run', '--project', str(work / 'check.csproj'),
            '-c', 'Release', '--', str(work / 'cases.json'), str(work / 'actual.json')],
            cwd=ROOT, env=dotnet_env.apply(os.environ, ROOT), capture_output=True,
            encoding='utf-8', errors='replace', timeout=180)
        if result.returncode:
            print('FAIL: C# fleet dispatch harness exited', result.returncode)
            print((result.stdout + result.stderr).replace(str(ROOT), '<repo>')[-3000:])
            return 1
        actual = json.loads((work / 'actual.json').read_text(encoding='utf-8'))
    expected = {case['name']: case for case in cases}
    failures, seen = [], set()
    for row in actual:
        identity = row['name'], row['host']
        if identity in seen:
            failures.append(f'duplicate result {identity}')
        seen.add(identity)
        case = expected[row['name']]
        for key, value in case['expect'].items():
            if row.get(key) != value:
                failures.append(f'{identity}: {key}={row.get(key)!r}, upstream={value!r}')
        if case['mode'] == 'incomplete' and '计划不完整' not in (row['blocked'] or ''):
            failures.append(f'{identity}: incomplete override was not rejected')
        if case['mode'] == 'incomplete' and row['static_first_implemented']:
            failures.append(f'{identity}: static surface claimed incomplete override was executable')
        if case['mode'] != 'base' and row['host'] == 'recording' and 'clear_boss' in row['invoked_ops']:
            failures.append(f'{identity}: bypassed override with registered primitive')
    if seen != {(case['name'], host) for case in cases for host in ('recording', 'device')}:
        failures.append('missing or unexpected host cases')
    print(f'[fleet dispatch] {len(cases)} native cases / {len(actual)} C# host runs; {len(failures)} failures')
    for failure in failures[:12]:
        print('  FAIL', failure)
    return bool(failures)


if __name__ == '__main__':
    raise SystemExit(main())
