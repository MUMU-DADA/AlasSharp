"""Compare the Core idle projection with AzurPilot's real overview method.

Only config storage, process status and the clock are fixture dependencies.
The reference method and priority parser are compiled unchanged from source.
No accounts, native hosts, devices or network time service are used.
"""
from __future__ import annotations

import argparse
import ast
from datetime import datetime
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from types import SimpleNamespace, ModuleType
from typing import Any
from unittest.mock import patch
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
NOW = datetime(2030, 1, 2, 3, 4, 5)


def references(source):
    priority_tree = ast.parse((source / 'module/config/task_priority.py').read_text(encoding='utf-8'))
    parser = next(node for node in priority_tree.body if isinstance(node, ast.FunctionDef) and node.name == 'parse_task_priority')
    namespace = {'re': re, 'Any': Any, 'ProcessManager': SimpleNamespace(_processes={}),
                 'STATES': {1: 'running', 2: 'stopped', 3: 'error', 4: 'updating'}}
    exec(compile(ast.Module([parser], []), '<upstream-priority>', 'exec'), namespace)
    tree = ast.parse((source / 'module/api/runtime_service.py').read_text(encoding='utf-8'))
    service = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == 'RuntimeService')
    overview = next(node for node in service.body if isinstance(node, ast.FunctionDef) and node.name == 'overview')
    exec(compile(ast.Module([overview], []), '<upstream-overview>', 'exec'), namespace)
    return namespace


HARNESS = r'''
using System.Text.Json.Nodes;
using Alas.Runtime;
var inputs = JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray();
var results = new JsonArray();
foreach (var input in inputs)
    results.Add(InstanceOverview.FromConfig(new ConfigSnapshot("fixture", "revision", input!.AsObject()),
        new DateTime(2030, 1, 2, 3, 4, 5)));
Console.WriteLine(results.ToJsonString());
'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--upstream', type=Path, default=ROOT.parent / 'others fork version/AzurPilot')
    args = parser.parse_args()
    native = references(args.upstream)
    clock = ModuleType('module.config.time_source')
    clock.now = lambda: NOW
    priority = ModuleType('module.config.task_priority')
    priority.parse_task_priority = native['parse_task_priority']
    fixtures = []
    for order in ('', 'Research > Reward', 'Reward＞Reward❯Unknown # comment\nResearch', ' # none\n',
                  'Future > Reward > Research', 'Research﹥Reward›Other', 'Other˃ResearchᐳReward', 'Unknown'):
        fixtures.append({
            'Alas': {'Emulator': {'ServerName': 'cn'}},
            'General': {'YukikazeTaskManager': {'TaskPriorityAdjustment': order}},
            'Reward': {'Scheduler': {'Enable': True, 'NextRun': '2020-01-01 00:00:00'}},
            'Research': {'Scheduler': {'Enable': True, 'NextRun': '2030-01-02T03:04:05'}},
            'Disabled': {'Scheduler': {'Enable': False, 'NextRun': '2000-01-01 00:00:00'}},
            'Future': {'Scheduler': {'Enable': True, 'NextRun': '2031-01-01 00:00:00'}},
            'Sooner': {'Scheduler': {'Enable': True, 'NextRun': '2030-01-02 03:04:06'}},
            'Other': {'Scheduler': {'Enable': True}},
            'Dashboard': {'Oil': {'Value': 0, 'Limit': 25000, 'Record': '2026-01-01 00:00:00'},
                          'ActionPoint': {'Value': 100, 'Total': 160, 'Record': '2020-01-01 00:00:00'},
                          'Unknown': {'Value': 42}, 'NotResource': {'Limit': 1}},
        })
    local = ROOT / '.runtime/verification'
    local.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='overview-', dir=local) as folder:
        work = Path(folder)
        (work / 'fixtures.json').write_text(json.dumps(fixtures), encoding='utf-8')
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        project = work / 'OverviewProbe.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
<ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" /></ItemGroup></Project>''', encoding='utf-8')
        dotnet = ROOT / '.runtime/dotnet/dotnet.exe'
        env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(ROOT / '.runtime/dotnet-home'),
                   NUGET_PACKAGES=str(ROOT / '.runtime/nuget/packages'))
        build = subprocess.run([str(dotnet), 'build', str(project), '-c', 'Release', '--source',
                                str(ROOT / '.runtime/nuget/source'), '-p:NuGetAudit=false'],
                               env=env, capture_output=True, timeout=120)
        assert build.returncode == 0, (build.stdout + build.stderr).decode(errors='replace')
        result = subprocess.run([str(dotnet), str(work / 'bin/Release/net10.0/OverviewProbe.dll'), str(work / 'fixtures.json')],
                                env=env, capture_output=True, timeout=30)
        assert result.returncode == 0, result.stderr.decode(errors='replace')
        actual = json.loads(result.stdout)
    with patch.dict(sys.modules, {'module.config.time_source': clock, 'module.config.task_priority': priority}):
        for data, core in zip(fixtures, actual, strict=True):
            configs = SimpleNamespace(read=lambda _: (data, 'revision'), translate=lambda key: key)
            expected = native['overview'](SimpleNamespace(configs=configs), 'fixture')
            for state in ('pending', 'waiting'):
                assert core[state] == [{'name': item['name'], 'next_run': item['nextRun']}
                                       for item in expected['tasks'] if item['state'] == state], (state, core)
            assert core['resources'] == [{key: value for key, value in item.items() if key != 'label'}
                                         for item in expected['resources']]
            assert core['emulator'] == expected['emulator']
    print(f'PASS: {len(fixtures)} Core/upstream overview comparisons, stable priority, comments/separators, due boundary and resource records')


if __name__ == '__main__':
    main()
