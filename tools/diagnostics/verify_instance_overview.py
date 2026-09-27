"""Verify the Engine idle projection ignores retired upstream scheduler state."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]

HARNESS = r'''
using System.Text.Json.Nodes;
using Alas.Engine.Runtime;
var inputs = JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray();
var results = new JsonArray();
foreach (var input in inputs)
    results.Add(InstanceOverview.FromConfig(new ConfigSnapshot("fixture", "revision", input!.AsObject()),
        new DateTime(2030, 1, 2, 3, 4, 5)));
Console.WriteLine(results.ToJsonString());
'''


def main() -> int:
    fixtures = []
    for index in range(8):
        fixtures.append({
            "Alas": {"Emulator": {"ServerName": "cn"}},
            "General": {"YukikazeTaskManager": {"TaskPriorityAdjustment": "Reward > Research"}},
            # Deliberately present: Engine must not project this retired state.
            "Reward": {"Scheduler": {"Enable": True, "NextRun": "2020-01-01 00:00:00"}},
            "Research": {"Scheduler": {"Enable": True, "NextRun": "2030-01-02T03:04:05"}},
            "Dashboard": {
                "Oil": {"Value": index, "Limit": 25000, "Record": "2026-01-01 00:00:00"},
                "ActionPoint": {"Value": 100, "Total": 160, "Record": "2020-01-01 00:00:00"},
                "Unknown": {"Value": 42}, "NotResource": {"Limit": 1},
            },
        })
    local = ROOT / ".runtime/verification"
    local.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="engine-overview-", dir=local) as folder:
        work = Path(folder)
        (work / "fixtures.json").write_text(json.dumps(fixtures), encoding="utf-8")
        (work / "Program.cs").write_text(HARNESS, encoding="utf-8")
        project = work / "OverviewProbe.csproj"
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
<ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Engine/Alas.Engine.csproj'))}" /></ItemGroup></Project>''', encoding="utf-8")
        dotnet = dotnet_env.executable(ROOT)
        env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(ROOT / ".runtime/dotnet-home"),
                   NUGET_PACKAGES=str(ROOT / ".runtime/nuget/packages"))
        build = subprocess.run([str(dotnet), "build", str(project), "-c", "Release", "-p:NuGetAudit=false"],
                               env=env, capture_output=True, timeout=120)
        assert build.returncode == 0, (build.stdout + build.stderr).decode(errors="replace")
        result = subprocess.run([str(dotnet), str(work / "bin/Release/net10.0/OverviewProbe.dll"), str(work / "fixtures.json")],
                                env=env, capture_output=True, timeout=30)
        assert result.returncode == 0, result.stderr.decode(errors="replace")
        actual = json.loads(result.stdout)
    for index, (input_data, projection) in enumerate(zip(fixtures, actual, strict=True)):
        assert projection["pending"] == [] and projection["waiting"] == [], (index, projection)
        expected_resources = [
            {"name": "Oil", "value": input_data["Dashboard"]["Oil"]["Value"], "limit": 25000,
             "total": None, "record": "2026-01-01 00:00:00"},
            {"name": "ActionPoint", "value": 100, "limit": None, "total": 160, "record": "2020-01-01 00:00:00"},
            {"name": "Unknown", "value": 42, "limit": None, "total": None, "record": None},
        ]
        assert projection["resources"] == expected_resources, (index, projection["resources"])
        assert projection["emulator"] == input_data["Alas"]["Emulator"], (index, projection)
    print(f"PASS: {len(fixtures)} Engine overview projections ignore retired scheduler fields and preserve resources")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
