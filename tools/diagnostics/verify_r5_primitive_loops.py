#!/usr/bin/env python3
"""Native loop/state feedback comparisons, plus explicit experimental budget rejection.

State frames are scripted inputs shared by both sides; actions only advance the
frame cursor. They do not emulate game success. No devices are used.
"""
from __future__ import annotations

import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]
HARNESS = r'''
using Alas.Campaign;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var results = new JsonArray();
foreach (var item in JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray()) {
    var frames = item!["frames"]!.Deserialize<CampaignGrid[][]>()!;
    var plan = item["plan"]!.Deserialize<CampaignPlan>()!;
    var config = new CampaignRuntimeConfig(Fleet2:true, MapHasMovableEnemy:true);
    var channel = new FrameChannel(frames);
    bool record = item["record"]!.GetValue<bool>();
    var recorder = new RecordingCampaignHost(frames[0], config);
    ICampaignPrimitiveHost host = record ? recorder : new DeviceCampaignHost(channel, frames[0], config);
    var result = CampaignHookRunner.Run(plan, plan.Header.Battles[0], host);
    var actions = record ? recorder.Actions.Select(a => Regex.Replace(a, @"\(([^,)]+).*", ":$1")).ToArray()
                         : channel.Actions.ToArray();
    results.Add(new JsonObject {
        ["name"] = item["name"]!.DeepClone(), ["completed"] = result.Completed,
        ["return"] = result.ReturnValue, ["blocked"] = result.BlockedReason,
        ["actions"] = JsonSerializer.SerializeToNode(actions),
    });
}
File.WriteAllText(args[1], results.ToJsonString());

sealed class FrameChannel(CampaignGrid[][] frames) : ICampaignCallChannel {
    int cursor;
    public List<string> Actions { get; } = [];
    public IReadOnlyList<CampaignGrid> ReadGrids() => frames[cursor];
    public JsonNode? Read(string name) => throw new InvalidOperationException($"Unexpected read: {name}");
    public void Set(string name, JsonNode? value) => throw new InvalidOperationException($"Unexpected write: {name}");
    public JsonNode? Call(string name, IReadOnlyList<JsonNode?> args,
                         IReadOnlyList<KeyValuePair<string, JsonNode?>> kwargs) {
        if (name == "logger.info") return null;
        if (name is not ("goto" or "clear_chosen_mystery" or "clear_chosen_enemy"))
            throw new InvalidOperationException($"Unexpected action: {name}");
        Actions.Add(name + ":" + args[0]!.GetValue<string>().TrimStart('#'));
        cursor = Math.Min(cursor + 1, frames.Length - 1);
        return name == "clear_chosen_enemy" ? JsonValue.Create(true) : null;
    }
}
'''


def build_cases():
    sys.path.insert(0, str(Path(os.environ.get('ALAS_REPO') or ROOT / '.runtime/engine')))
    from module.base.utils import location2node, node2location
    from module.map.map import Map
    from module.map.map_base import CampaignMap
    from module.map.map_grids import SelectedGrids
    from types import SimpleNamespace

    def grid(name, **flags):
        return dict(Location=name, IsMystery=False, IsSiren=False, Cost=0, Cost1=0, Cost2=5, **flags)

    def frame(mystery=False, siren=False, approaching=False):
        grids = [grid('A1'), grid('B1'), grid('C1')]
        grids[0]['IsMystery'] = mystery  # Explicitly ignored in mystery cases.
        grids[1].update(IsMystery=mystery, IsSiren=siren, Cost2=1 if approaching else 5)
        grids[2]['Cost2'] = 2  # Movement candidate and post-call marker.
        return grids

    class Probe(Map):
        def __init__(self, frames):
            self.frames, self.cursor, self.actions = frames, 0, []
            self.config = SimpleNamespace(FLEET_2=True, MAP_HAS_MOVABLE_ENEMY=True,
                                          MAP_HAS_MOVABLE_NORMAL_ENEMY=False)
            self.map = CampaignMap('loop-fixture')
            self.map.shape = 'C1'
            self.load_frame()

        def load_frame(self):
            mapping = {'IsMystery': 'is_mystery', 'IsSiren': 'is_siren',
                       'Cost': 'cost', 'Cost1': 'cost_1', 'Cost2': 'cost_2'}
            for fields in self.frames[self.cursor]:
                cell = self.map[node2location(fields['Location'])]
                for key, attr in mapping.items():
                    setattr(cell, attr, fields[key])

        def advance(self, name, cell):
            self.actions.append(name + ':' + location2node(cell.location))
            self.cursor = min(self.cursor + 1, len(self.frames) - 1)
            self.load_frame()

        def clear_chosen_mystery(self, cell):
            self.advance('clear_chosen_mystery', cell)

        def clear_chosen_enemy(self, cell, expected=''):
            self.advance('clear_chosen_enemy', cell)
            return True

        def goto(self, cell):
            self.advance('goto', cell)

    cases = []

    def add(name, op, frames, record=False, guard=False):
        call_args = {'keyword': {'ignore': {'__grids__': [[0, 0]]}}} if op == 'clear_all_mystery' else {}
        steps = [{'kind': 'assign', 'op': op, 'target': 'answer', 'args': call_args},
                 {'kind': 'call', 'op': 'goto', 'args': {'positional': [{'__grid__': [2, 0]}]}},
                 {'kind': 'branch', 'test': {'local': 'answer'}, 'body': [{'kind': 'return', 'value': True}],
                  'orelse': [{'kind': 'return', 'value': False}]}]
        if guard:
            expected = dict(completed=False, return_value=None, actions=['clear_chosen_mystery:B1'] * 100)
        else:
            probe = Probe(frames)
            kwargs = {'ignore': SelectedGrids([probe.map[(0, 0)]])} if op == 'clear_all_mystery' else {}
            value = getattr(probe, op)(**kwargs)
            probe.goto(probe.map[(2, 0)])
            expected = dict(completed=True, return_value=value, actions=probe.actions)
        cases.append(dict(name=name, frames=frames, record=record, guard=guard, expect=expected,
                          plan={'campaign': {'battles': [{'method': 'battle_0', 'plan_complete': True,
                                                         'steps': steps}]}}))

    # Keep GridInfo identities stable when applying frames: ignore uses objects.
    for count in (0, 1, 2, 99, 100):
        frames = [frame(mystery=True) for _ in range(count)] + [frame()]
        add(f'mystery-cleared-after-{count}', 'clear_all_mystery', frames)
    add('mystery-static-channel-budget', 'clear_all_mystery', [frame(mystery=True)], guard=True)
    add('mystery-static-recorder-budget', 'clear_all_mystery', [frame(mystery=True)], record=True, guard=True)
    add('mystery-still-present-after-100', 'clear_all_mystery',
        [frame(mystery=True)] * 101 + [frame()], guard=True)
    for moves in (0, 1, 2, 19, 20):
        add(f'protect-approaching-after-{moves}', 'fleet_2_protect',
            [frame(siren=True)] * moves + [frame(siren=True, approaching=True)])
    for moves in (0, 1, 5):
        add(f'protect-disappeared-after-{moves}', 'fleet_2_protect',
            [frame(siren=True)] * moves + [frame()])
    for record in (False, True):
        add(f'protect-static-{record}', 'fleet_2_protect', [frame(siren=True)], record=record)
    return cases


def main():
    with contextlib.redirect_stdout(io.StringIO()):
        cases = build_cases()
    runtime = ROOT / '.runtime/verification'
    runtime.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='primitive-loops-', dir=runtime) as folder:
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
            print('FAIL: C# primitive-loop harness exited', result.returncode)
            print((result.stdout + result.stderr).replace(str(ROOT), '<repo>')[-3000:])
            return 1
        actual = json.loads((work / 'actual.json').read_text(encoding='utf-8'))
    by_name = {case['name']: case for case in cases}
    failures, seen = [], set()
    for row in actual:
        name = row['name']
        if name in seen or name not in by_name:
            failures.append(f'duplicate/unknown result: {name}')
            continue
        seen.add(name)
        case = by_name[name]
        for key, value in case['expect'].items():
            if row['return' if key == 'return_value' else key] != value:
                failures.append(f'{name}: {key} mismatch')
        if case['guard'] and 'clear_all_mystery' not in (row['blocked'] or ''):
            failures.append(f'{name}: missing specific budget refusal')
    if seen != set(by_name):
        failures.append('missing case results')
    if failures:
        print('FAIL:', '; '.join(failures))
        return 1
    guards = sum(case['guard'] for case in cases)
    print(f'PASS: {len(cases) - guards} native loop/state comparisons + {guards} budget refusals; no devices')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
