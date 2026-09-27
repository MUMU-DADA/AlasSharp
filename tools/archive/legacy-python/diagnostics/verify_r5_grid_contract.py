"""Validate the native grid snapshot contract without importing a device host."""
from __future__ import annotations

import argparse
import ast
import json
import os
from pathlib import Path
import subprocess
import tempfile
import traceback
from types import SimpleNamespace
from xml.sax.saxutils import escape

import dotnet_env
import numpy as np

ROOT = Path(__file__).resolve().parents[2]

HARNESS = r'''
using Alas.Campaign;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool value, string why) { checks++; if (!value) throw new Exception(why); }
void Reject(JsonNode? value, string why, string? expected = null) {
    checks++;
    try { CampaignMapState.FromUpstream(value, out _); }
    catch (InvalidDataException error) {
        if (expected is null || error.Message.Contains(expected, StringComparison.Ordinal)) return;
        throw new Exception(why + ": wrong rejection: " + error.Message);
    }
    throw new Exception(why);
}
var payload = JsonNode.Parse(File.ReadAllText(args[0]))!;
var grids = CampaignMapState.FromUpstream(payload, out var unknown);
Check(unknown.Count == 0 && grids.Count == 2, "native snapshot lost flags/grids");
Check(grids[0] is { Location: "A1", IsSpawnPoint: true, IsSubmarineSpawnPoint: false,
    IsFlare: true, IsMechanismTrigger: true, IsEnemy: true, MayEnemy: true,
    Cost: 2, Cost1: 3, Cost2: 4, Weight: 50, EnemyScale: 2, EnemyGenre: "Main" }, "native scalar/flag mismatch");
Check(grids[1] is { Location: "B1", IsSubmarineSpawnPoint: true, IsSpawnPoint: false,
    Weight: 1, Cost: 9999, EnemyScale: 0, EnemyGenre: null }, "native defaults/null changed");
var spawn = CampaignGridTokens.FromToken("A1", "SP");
var submarine = CampaignGridTokens.FromToken("B1", "__");
Check(spawn.IsSpawnPoint && !spawn.IsSubmarineSpawnPoint && spawn.Encode() == "--", "SP decoding/encoding changed");
Check(submarine.IsSubmarineSpawnPoint && !submarine.IsSpawnPoint && submarine.Encode() == "--", "submarine spawn decoding/encoding changed");
var host = new RecordingCampaignHost([spawn, submarine]);
Check(CampaignPrimitives.MapSelect(host, new Dictionary<string, bool> { ["is_spawn_point"] = true }).Count == 1,
    "map.select lost spawn flag");
Check(CampaignPrimitives.MapSelect(host, new Dictionary<string, bool> { ["is_submarine_spawn_point"] = true }).Count == 1,
    "map.select lost submarine spawn flag");
host.SetGridFlag(spawn, "is_spawn_point", false);
host.SetGridFlag(submarine, "is_submarine_spawn_point", false);
Check(host.Grids.All(g => !g.IsSpawnPoint && !g.IsSubmarineSpawnPoint), "ApplyFlag did not clear spawn flags");
var overlay = CampaignMapState.OverlayDetection([spawn with { IsEnemy = true }, submarine],
    new Dictionary<string, List<string>> { ["0,0"] = [], ["1,0"] = ["is_fleet"] }, out unknown);
Check(overlay[0].IsEnemy && overlay[0].IsSpawnPoint && overlay[1].IsSubmarineSpawnPoint && overlay[1].IsFleet,
    "positive-only detection overlay cleared prior state");
foreach (var field in new[] { "flags", "cost", "cost_1", "cost_2", "weight", "enemy_scale", "enemy_genre" }) {
    var missing = payload.DeepClone(); missing["grids"]![0]!.AsObject().Remove(field);
    Reject(missing, "missing " + field + " was defaulted");
}
foreach (var field in new[] { "cost", "cost_1", "cost_2", "weight", "enemy_scale" }) {
    foreach (var bad in new JsonNode?[] { null, JsonValue.Create(true), JsonValue.Create("1"), JsonValue.Create(1.5) }) {
        var invalid = payload.DeepClone(); invalid["grids"]![0]![field] = bad;
        Reject(invalid, "bad " + field + " accepted");
    }
}
foreach (var bad in new JsonNode?[] { null, JsonValue.Create("is_enemy"), new JsonArray(true), new JsonArray("") }) {
    var invalid = payload.DeepClone(); invalid["grids"]![0]!["flags"] = bad;
    Reject(invalid, "bad flags accepted");
}
var badGenre = payload.DeepClone(); badGenre["grids"]![0]!["enemy_genre"] = 2;
Reject(badGenre, "non-string genre accepted");
var duplicate = payload.DeepClone(); duplicate["grids"]!.AsArray().Add(duplicate["grids"]![0]!.DeepClone());
Reject(duplicate, "duplicate full grid accepted", "重复坐标");
foreach (var coordinate in new[] { "[null,0]", "[true,0]", "[0.5,0]", "[-1,0]", "[0,2147483647]" }) {
    var invalid = payload.DeepClone(); invalid["grids"]![0]!["loca"] = JsonNode.Parse(coordinate);
    Reject(invalid, "invalid coordinate accepted");
}
Reject(JsonNode.Parse("{\"error\":\"snapshot failed\"}"), "error response accepted");
Console.WriteLine($"PASS: C# grid contract {checks} checks");
'''


def load_definitions(path: Path, names: set[str], namespace: dict) -> None:
    tree = ast.parse(path.read_text(encoding='utf-8'))
    selected = [node for node in tree.body if isinstance(node, (ast.ClassDef, ast.FunctionDef))
                and node.name in names]
    if {node.name for node in selected} != names:
        raise AssertionError(f'missing definitions in {path.name}')
    exec(compile(ast.Module(body=selected, type_ignores=[]), str(path), 'exec'), namespace)


def python_contract(upstream: Path) -> dict:
    # Load the actual producer and native GridInfo, without alas_vision import/chdir/device setup.
    namespace = {'_CAMPAIGN': {}, 'traceback': traceback}
    load_definitions(ROOT / 'tools/alas_vision.py', {'_campaign_grid_int', 'op_s3_campaign_grids'}, namespace)
    native = {'location2node': lambda location: f'{chr(65 + location[0])}{location[1] + 1}'}
    load_definitions(upstream / 'module/map_detection/grid_info.py', {'GridInfo'}, native)
    grid_type = native['GridInfo']
    first, second = grid_type(), grid_type()
    first.location, second.location = (0, 0), (1, 0)
    first.decode('SP')
    second.decode('__')
    first.is_enemy = True
    first.may_enemy = True
    first.is_flare = np.bool_(True)
    first.is_mechanism_trigger = True
    first.weight, first.enemy_scale, first.enemy_genre = 50.0, 2, 'Main'
    first.cost, first.cost_1, first.cost_2 = np.int64(2), 3, 4
    grid_map = SimpleNamespace(grids={(0, 0): first, (1, 0): second}, shape=(1, 0))
    namespace['_CAMPAIGN']['obj'] = SimpleNamespace(map=grid_map)
    export = namespace['op_s3_campaign_grids']
    snapshot = export({})
    assert 'error' not in snapshot, snapshot
    assert snapshot['grids'][1]['enemy_genre'] is None
    assert snapshot['grids'][1]['weight'] == 1
    assert type(snapshot['grids'][0]['weight']) is int
    checks = 4
    original_grid = first

    class Missing:
        def __init__(self, field):
            self.field = field

        def __getattr__(self, name):
            if name == self.field:
                raise AttributeError(name)
            return getattr(original_grid, name)

    for field in ('weight', 'enemy_scale', 'enemy_genre', 'cost', 'cost_1', 'cost_2', 'is_spawn_point'):
        grid_map.grids[(0, 0)] = Missing(field)
        rejected = export({})
        assert 'error' in rejected and field in rejected['error'] and 'grids' not in rejected, rejected
        checks += 1
    grid_map.grids[(0, 0)] = first
    for field in ('weight', 'enemy_scale', 'cost', 'cost_1', 'cost_2'):
        original = getattr(first, field)
        for bad in (None, True, '1', 1.5, float('nan'), float('inf'), 2147483648):
            setattr(first, field, bad)
            rejected = export({})
            assert 'error' in rejected and 'grids' not in rejected, (field, bad, rejected)
            checks += 1
        setattr(first, field, original)
    for field, bad in (('enemy_genre', 2), ('is_spawn_point', 'false'), ('is_flare', 1)):
        original = getattr(first, field)
        setattr(first, field, bad)
        rejected = export({})
        assert 'error' in rejected and 'grids' not in rejected, (field, rejected)
        setattr(first, field, original)
        checks += 1
    assert export({}) == snapshot
    print(f'PASS: Python grid contract {checks} checks')
    return snapshot


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--upstream', type=Path, default=Path(os.environ.get('ALAS_REPO', ROOT / '.runtime/engine')))
    args = parser.parse_args()
    snapshot = python_contract(args.upstream)
    parent = ROOT / '.runtime/verification'
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='grid-contract-', dir=parent) as temporary:
        path = Path(temporary)
        (path / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        (path / 'snapshot.json').write_text(json.dumps(snapshot, allow_nan=False), encoding='utf-8')
        project = f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>
<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup><ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" />
</ItemGroup></Project>'''
        (path / 'check.csproj').write_text(project, encoding='utf-8')
        subprocess.run([str(dotnet_env.executable(ROOT)), 'run', '--project', str(path / 'check.csproj'),
                        '-c', 'Release', '--', str(path / 'snapshot.json')], cwd=ROOT,
                       env=dotnet_env.apply(os.environ, ROOT), check=True, timeout=180)


if __name__ == '__main__':
    main()
