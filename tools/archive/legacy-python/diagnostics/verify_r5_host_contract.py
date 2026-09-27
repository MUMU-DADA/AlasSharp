"""Exercise the native state seam and C# consumer with failures, without devices."""
from __future__ import annotations
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
sys.path.insert(0, str(ROOT / 'tools'))

HARNESS = r'''
using Alas.Campaign;
using Alas.Vision;
using System.Text.Json;
using System.Text.Json.Nodes;

void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Reject(Action action, string message) {
    try { action(); } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; }
    throw new Exception(message);
}
var engine = new Stub();
var channel = new VisionCampaignCallChannel(engine);
foreach (string response in new[] { "{\"error\":\"native failure\"}", "{\"refused\":true,\"reason\":\"fixture\"}", "{}" }) {
    engine.Response = JsonNode.Parse(response)!;
    Reject(() => channel.Call("goto", [], []), "call failure accepted");
    Reject(() => channel.Read("battle_count"), "read failure accepted");
    Reject(() => channel.Set("ammo_count", JsonValue.Create(2)), "write failure accepted");
}
engine.Response = JsonNode.Parse("{\"value\":false}")!;
Check(channel.Call("clear_chosen_enemy", [], [])!.GetValue<bool>() == false, "false became true");
engine.Response = JsonNode.Parse("{\"value\":\"C2\",\"callable\":false}")!;
Check(channel.Read("fleet_1_location")!.GetValue<string>() == "C2", "state lost");
Check(engine.Last!["read_only"]!.GetValue<bool>(), "state read not marked read-only");
foreach (string kind in new[] { "CampaignEnd", "MapEnemyMoved" }) {
    engine.Response = kind == "CampaignEnd" ? JsonNode.Parse("{\"campaign_end\":true,\"reason\":\"Withdraw\",\"outcome\":\"withdrawn\"}")!
        : JsonNode.Parse("{\"control_flow\":\"MapEnemyMoved\"}")!;
    bool signaled = false;
    try { channel.Call("goto", [], []); } catch (CampaignControlFlowSignal e) { signaled = e.Kind == kind; }
    Check(signaled, "native control flow lost");
}
var record = new RecordingCampaignCallChannel(new Dictionary<string, JsonNode?> {
    ["mystery_count"] = JsonValue.Create(7), ["ammo_count"] = JsonValue.Create(3), ["fleet_ammo"] = JsonValue.Create(2),
    ["fleet_current_index"] = JsonValue.Create(1),
    ["picked_flare"] = new JsonArray("A1"), ["picked_light_house"] = new JsonArray(),
}).WithGrids([new CampaignGrid("A1", IsCaughtBySiren:true)]);
var host = new DeviceCampaignHost(record, record.ReadGrids());
host.ClearCaughtBySirenFlags();
Check(record.Calls.Single().Name == "set:map.A1.is_caught_by_siren", "flag not synchronized");
Check(!host.EnsureFleet(1) && host.EnsureFleet(2) && !host.EnsureFleet(2), "fleet_ensure return value lost");
Check(record.Calls.Last().Name == "fleet_ensure" && host.FleetCurrentIndex == 2, "fleet change used switch_to instead of fleet_ensure");
host.AmmoCount = 1; host.FleetAmmo = 4;
Check(host.AmmoCount == 1 && host.FleetAmmo == 4 && host.MysteryCount == 7, "native counters lost");
Reject(() => { _ = host.BattleCount; }, "missing counter fabricated as zero");
Check(host.PickedFlare.Contains("A1") && !host.PickedFlare.Add("A1"), "existing native pickup ignored");
Check(host.PickedLightHouse.Add("A1") && record.Read("picked_light_house")!.AsArray()[0]!.GetValue<string>() == "A1", "pickup not synchronized");
host.ClearChosenEnemy(host.Grids[0], "boss", "boss");
Check(record.Calls.Last().Name == "fleet_boss.clear_chosen_enemy", "boss property binding lost");
Reject(() => new DeviceCampaignHost(new RecordingCampaignCallChannel(), []).RefreshFromUpstream(), "empty state accepted");
var failedHost = new DeviceCampaignHost(channel, [new CampaignGrid("A1")]);
engine.Response = JsonNode.Parse("{\"error\":\"write rejected\"}")!;
Reject(() => failedHost.SetGridFlag(failedHost.Grids[0], "is_flare", true), "write failure lost");
Check(!failedHost.Grids[0].IsFlare, "local state changed after failed write");
engine.Response = JsonNode.Parse("{\"value\":null}")!;
Reject(failedHost.Withdraw, "return without CampaignEnd claimed exit");
Check(!failedHost.EndRequested, "failed withdrawal marked ended");
engine.Response = JsonNode.Parse("{\"value\":[],\"callable\":false}")!;
Reject(() => failedHost.PickedFlare.Add("A1"), "unconfirmed pickup accepted");
Check(failedHost.PickedFlare.Count == 0, "failed pickup cached locally");
engine.Response = JsonNode.Parse("{\"error\":\"read failed\",\"exception_type\":\"MapDetectionError\"}")!;
Check(!failedHost.UpdateMap(), "typed map detection failure not available for native refocus recovery");
engine.Response = JsonNode.Parse("{\"error\":\"MapDetectionError text is not a type\",\"exception_type\":\"ValueError\"}")!;
Reject(() => failedHost.UpdateMap(), "unrelated error was converted into recoverable map detection failure");
var grids = CampaignMapState.FromUpstream(JsonNode.Parse("""{"grids":[{"loca":[0,0],"flags":["is_land","is_flare","may_enemy","may_boss","may_ammo","may_mystery","may_siren","may_ambush","is_mechanism_trigger"],"cost":3,"cost_1":4,"cost_2":5,"weight":6,"enemy_scale":2,"enemy_genre":"Main"}]}"""), out var unknown);
Check(unknown.Count == 0 && grids[0] is { IsLand:true, IsFlare:true, MayEnemy:true, MayBoss:true, MayAmmo:true, MayMystery:true, MaySiren:true, MayAmbush:true, IsMechanismTrigger:true, Cost:3, Cost1:4, Cost2:5 }, "native flags/costs lost");
Reject(() => CampaignMapState.FromUpstream(JsonNode.Parse("""{"grids":[{"loca":[0,0],"flags":[],"cost":3,"cost_1":4,"cost_2":5,"weight":6,"enemy_scale":2}]}"""), out _), "missing native genre fabricated");
Reject(() => CampaignMapState.FromUpstream(JsonNode.Parse("{\"error\":\"uninitialized\"}"), out _), "error became empty map");
var duplicate = JsonNode.Parse("""{"grids":[{"loca":[0,0],"flags":[],"cost":3,"cost_1":4,"cost_2":5,"weight":6,"enemy_scale":2,"enemy_genre":"Main"}]}""")!;
duplicate["grids"]!.AsArray().Add(duplicate["grids"]![0]!.DeepClone());
Reject(() => CampaignMapState.FromUpstream(duplicate, out _), "duplicate coordinates accepted");
var refocus = new RecordingCampaignHost([new CampaignGrid("A1")]) { UpdateMapSucceeds = false };
bool mapError = false;
try { CampaignPrimitives.HandleBossAppearRefocus(refocus, null); } catch (NotSupportedException) { mapError = true; }
Check(mapError, "refocus used stale map after detection failure without preset");
var step = JsonSerializer.Deserialize<CampaignPlanStep>("""{"kind":"call","op":"clear_enemy","args":{"positional":[],"keyword":{"scale":{"__tuple__":[1,2]}}}}""")!;
var translated = CampaignCallTranslator.Translate(step);
Check(translated.Unsupported is null && translated.KeywordArgs.Single().Value!["__tuple__"]!.AsArray().Count == 2, "tuple contract lost");
Console.WriteLine("PASS: Core call errors, control flow, authoritative counters, transactional writes and map flags");
sealed class Stub : VisionEngineBase {
    public JsonNode Response = new JsonObject();
    public JsonNode? Last;
    protected override JsonNode CallRaw(string op, object? args) {
        Last = JsonSerializer.SerializeToNode(args); return Response.DeepClone();
    }
}
'''


def main():
    import alas_vision as av
    from module.map.map_base import CampaignMap
    from module.exception import MapEnemyMoved, MapDetectionError
    from module.logger import logger
    from s3_campaign_execution import run_native_campaign
    from s3_stub_campaign import NativeRunCampaign

    grid_map = CampaignMap('offline-seam')
    grid_map.shape = 'B1'
    grid_map.map_data = '-- --'
    grid_map.load_map_data()
    instance = SimpleNamespace(map=grid_map, fleet_1_location=(2, 1), fleet_2_location=(),
                               camera=(0, 0), fleet_current_index=1, ammo_count=3, fleet_ammo=5,
                               picked_flare=[grid_map[(0, 0)]], picked_light_house=[])
    original = dict(av._CAMPAIGN)
    av._CAMPAIGN['obj'] = instance
    try:
        for name, value in [('map.A1.is_flare', True), ('map.A1.is_caught_by_siren', False), ('ammo_count', 1),
                            ('map.A1.may_siren', True), ('map.A1.is_enemy', False)]:
            result = av.op_s3_campaign_call(dict(name=name, set=value, allow_actions=True))
            assert result.get('set') is True, result
        assert grid_map[(0, 0)].is_flare and grid_map[(0, 0)].may_siren and instance.ammo_count == 1
        for value in ('true', 1, False):
            assert av.op_s3_campaign_call(dict(name='ammo_count', set=1, allow_actions=value)).get('refused')
        assert av.op_s3_campaign_call(dict(name='config.anything', set=1, allow_actions=True)).get('error')
        assert av.op_s3_campaign_call(dict(name='ammo_count', set=True, allow_actions=True)).get('error')
        assert av.op_s3_campaign_call(dict(name='fleet_1_location', read_only=True))['value'] == 'C2'
        assert av.op_s3_campaign_call(dict(name='fleet_2_location', read_only=True))['value'] == ''
        assert av.op_s3_campaign_call(dict(name='fleet_current_index', read_only=True))['value'] == 1
        assert av.op_s3_campaign_call(dict(name='picked_flare', read_only=True))['value'] == ['A1']
        assert av.op_s3_campaign_call(dict(name='picked_light_house.append', args=['#B1'])).get('refused')
        assert av.op_s3_campaign_call(dict(name='picked_light_house.append', args=['#B1'], allow_actions=True))['value'] is None
        assert instance.picked_light_house == [grid_map[(1, 0)]]
        assert av.op_s3_campaign_call(dict(name='picked_light_house', read_only=True))['value'] == ['B1']
        instance.battle_count = lambda: (_ for _ in ()).throw(AssertionError('read invoked callable'))
        assert av.op_s3_campaign_call(dict(name='battle_count', read_only=True)).get('error')
        assert av.op_s3_campaign_call(dict(name='fleet_2.switch_to')).get('refused')
        for unsafe in ('device.click', 'update', 'focus_to', 'ensure_no_info_bar', 'pick_up_ammo', 'new_unknown_method'):
            assert av.op_s3_campaign_call(dict(name=unsafe)).get('refused'), unsafe
        assert av.op_s3_campaign_call(dict(name='logger.info', args=['offline state seam check'])).get('error') is None
        grid_map[(0, 0)].is_mechanism_trigger = True
        flags = av.op_s3_campaign_grids({})['grids'][0]['flags']
        assert 'is_flare' in flags and 'is_mechanism_trigger' in flags
        args = av._campaign_arg(instance, {'__tuple__': ['#A1', ['#B1']]})
        assert isinstance(args, tuple) and args[0] is grid_map[(0, 0)] and args[1][0] is grid_map[(1, 0)]
        def moved():
            raise MapEnemyMoved('fixture')
        instance.fixture_moved = moved
        assert av.op_s3_campaign_call(dict(name='fixture_moved', allow_actions=True))['control_flow'] == 'MapEnemyMoved'
        def detection_failed():
            raise MapDetectionError('fixture')
        instance.update = detection_failed
        error = av.op_s3_campaign_call(dict(name='update', allow_actions=True))
        assert error['exception_type'] == 'MapDetectionError' and error['traceback_tail']
        before = list(logger.handlers)
        native = NativeRunCampaign()
        native.config.MAP_CLEAR_ALL_THIS_TIME = False
        native.config.POOR_MAP_DATA = False
        result = run_native_campaign(native, max_rounds=1, max_seconds=5)
        assert result['shadow_observation']['source'] == 'native_run_logger/1'
        assert 'BATTLE_0' in result['shadow_observation']['lines']
        assert result['shadow_observation']['variants'] == ['default_hooks']
        assert list(logger.handlers) == before
    finally:
        av._CAMPAIGN.clear()
        av._CAMPAIGN.update(original)
    parent = ROOT / '.runtime/verification'
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='host-contract-', dir=parent) as tmp:
        path = Path(tmp)
        (path / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        project = f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>
<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup><ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" />
</ItemGroup></Project>'''
        (path / 'check.csproj').write_text(project, encoding='utf-8')
        subprocess.run([str(dotnet_env.executable(ROOT)), 'run', '--project', str(path / 'check.csproj'), '-c', 'Release'],
                       cwd=ROOT, env=dotnet_env.apply(os.environ, ROOT), check=True, timeout=180)
    print('PASS: native seam state/tuple/control-flow semantics; no devices')


if __name__ == '__main__':
    main()
