"""Current-run shadow evidence isolation and batch resilience, without a device."""
from __future__ import annotations

import json
import logging
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
from types import SimpleNamespace
from xml.sax.saxutils import escape

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from campaign_shadow_observation import CampaignShadowObservation


HARNESS = r'''
using Alas.Campaign;
using Alas.Runtime;
using Alas.Vision;
using System.Text.Json.Nodes;

string root = args[0], work = args[1];
Environment.SetEnvironmentVariable("ALAS_ENGINE_LOOP", "shadow");
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
var chapter = "campaign.campaign_main.campaign_1_1";
var source = JsonNode.Parse("""{"source":"native_run_logger/1","lines":["BATTLE_0","Using function: battle_0"],"variants":["default_hooks"]}""")!;
foreach (var name in new[] { "current", "missing", "malformed", "observer_failure", "dry_run" })
{
    string repo = Path.Combine(work, name, "repo");
    Directory.CreateDirectory(Path.Combine(repo, "log"));
    string daily = Path.Combine(repo, "log", "daily.txt");
    File.WriteAllText(daily, "BATTLE_999\nUsing function: unrelated_previous_sortie\n");
    using var locked = new FileStream(daily, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var response = JsonNode.Parse("""{"stage":"1-1","dry_run":false,"outcome":"incomplete","cleared":false,"stop_reason":"round_limit","steps":[]}""")!.AsObject();
    if (name != "missing") response["shadow_observation"] = source.DeepClone();
    if (name == "malformed") response["shadow_observation"]!["lines"] = "invalid";
    if (name == "dry_run") response["dry_run"] = true;
    var log = new SessionLog(echo: false);
    using var session = AlasSession.Start(new SessionOptions {
        RepoDirectory = repo, ToolsDirectory = Path.Combine(root, "tools"),
        DataDirectory = Path.Combine(root, "data"), DryRun = name == "dry_run", AllowActions = true,
        ArtifactsDirectory = Path.Combine(work, name, "runs"),
    }, _ => new Stub(response), log);
    if (name == "observer_failure") Directory.CreateDirectory(Path.Combine(session.RunDirectory!, "shadow-sortie-1-1.json"));
    var batch = new CampaignBatchRunner(session) { StopOnFailure = false }.Run(new[] { chapter, chapter });
    Check(batch.Stages.Count == 2 && File.Exists(batch.IndexPath), name + ": observer aborted batch/index");
    Check(batch.Stages.All(s => s.ContractViolations.Count == 0 && s.Outcome == "incomplete"), name + ": sortie verdict changed");
    var files = Directory.GetFiles(session.RunDirectory!, "shadow-*.json");
    if (name == "current") {
        Check(files.Length == 2, "repeated chapter overwrote its shadow artifact");
        foreach (var file in files) {
            var doc = JsonNode.Parse(File.ReadAllText(file))!;
            Check(doc["matched"]!.GetValue<int>() == 1 && doc["clean"]!.GetValue<bool>(), "old daily evidence leaked in");
        }
    } else if (name == "observer_failure") {
        Check(files.Length == 1 && log.Entries.Any(e => e.Scope == "shadow" && e.Level == "WARN"), "observer failure missing");
    } else Check(files.Length == 0, name + ": fabricated shadow artifact");
}
var plan = CampaignPlanReader.Read(Path.Combine(root, "data"), "campaign_main", "campaign_1_1");
Check(!CampaignShadow.Compare(plan, UpstreamLogParser.Parse("")).Clean, "empty evidence accepted");
Check(!CampaignShadow.Compare(plan, UpstreamLogParser.Parse("BATTLE_0\n")).Clean, "skipped evidence accepted");
Check(UpstreamLogParser.Parse("--- X1 ---").Rounds.Count == 0, "unrelated header accepted");
Console.WriteLine("PASS: current-run isolation, repeated attempts, dry-run, missing/malformed evidence and observer failure");
sealed class Stub(JsonObject response) : VisionEngineBase {
    protected override JsonNode CallRaw(string op, object? args) => response.DeepClone();
}
'''


def main():
    logger = logging.getLogger('shadow-fixture')
    logger.setLevel(logging.INFO)
    instance = SimpleNamespace(config=SimpleNamespace(MAP_CLEAR_ALL_THIS_TIME=False, POOR_MAP_DATA=False))
    observations = []
    for _ in range(2):
        observer = CampaignShadowObservation(instance)
        logger.addHandler(observer)
        try:
            logger.info('BATTLE_0')
            logger.info('Using function: battle_0')
            other = threading.Thread(target=lambda: logger.info('Using function: wrong_thread'))
            other.start()
            other.join()
        finally:
            logger.removeHandler(observer)
        observations.append(observer.document)
    assert observations[0] == observations[1]
    assert observations[0]['lines'] == ['BATTLE_0', 'Using function: battle_0']
    instance.config.POOR_MAP_DATA = True
    observer = CampaignShadowObservation(instance)
    observer.emit(logging.LogRecord('test', logging.INFO, '', 0, 'BATTLE_0', (), None))
    assert observer.document['variants'] == ['battle_with_poor_map_data']
    instance.config.POOR_MAP_DATA = 'false'
    observer = CampaignShadowObservation(instance)
    observer.emit(logging.LogRecord('test', logging.INFO, '', 0, 'BATTLE_0', (), None))
    assert 'error' in observer.document
    parent = ROOT / '.runtime/verification'
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='shadow-observation-', dir=parent) as temporary:
        path = Path(temporary)
        (path / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        project = f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>
<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup><ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" />
</ItemGroup></Project>'''
        (path / 'check.csproj').write_text(project, encoding='utf-8')
        env = dotnet_env.apply(os.environ, ROOT)
        subprocess.run([str(dotnet_env.executable(ROOT)), 'run', '--project', str(path / 'check.csproj'), '-c', 'Release',
                        '--', str(ROOT), str(path / 'work')], cwd=ROOT, env=env, check=True, timeout=180)


if __name__ == '__main__':
    main()
