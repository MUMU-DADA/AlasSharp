"""Compare deployment storage with original upstream functions, then test real HTTP.

Synthetic repositories only; no host, device, account or visible UI is started.
"""
from __future__ import annotations

import argparse
import ast
import copy
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
from typing import Any
from xml.sax.saxutils import escape

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from export_deploy_settings import declarations, export
from verify_config_api import request


def reference(source):
    namespace = declarations(source)
    namespace.update(json=json, Any=Any, re=re, is_demo_mode=lambda: False,
                     atomic_read_text=lambda path: Path(path).read_text(encoding='utf-8'))
    for path, names in [('module/runtime/deploy_settings.py', {'_parse_value', '_value_for_api', 'parse_run_config',
                                                            'format_run_config', 'deploy_settings_schema'}),
                        ('deploy/utils.py', {'poor_yaml_read'})]:
        tree = ast.parse((source / path).read_text(encoding='utf-8'))
        nodes = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name in names]
        assert len(nodes) == len(names)
        exec(compile(ast.Module(nodes, []), '<upstream-deploy-reference>', 'exec'), namespace)
    return namespace


HARNESS = r'''
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Runtime;
using Alas.Client;
using Alas.Contracts;
var inputs = JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray();
var output = new JsonArray();
int index = 0;
foreach (var input in inputs)
{
    string repo = Path.Combine(args[1], "case-" + index++);
    Directory.CreateDirectory(Path.Combine(repo, "config"));
    string file = Path.Combine(repo, "config", "deploy.yaml");
    File.WriteAllText(file, input!["text"]!.GetValue<string>());
    File.WriteAllText(Path.Combine(repo, "config", "fixture.json"), "{\"Alas\":{}}");
    File.WriteAllText(Path.Combine(repo, "config", "template.json"), "{\"Alas\":{}}");
    string before = File.ReadAllText(file);
    var workspace = new DeploySettingsWorkspace(repo, demo: () => input["demo"]?.GetValue<bool>() ?? false);
    var result = new JsonObject();
    try
    {
        result["result"] = input["operation"]?.GetValue<string>() switch
        {
            "startup" => workspace.ReadStartup("fixture"),
            "enable" => workspace.SetStartup("fixture", true),
            "disable" => workspace.SetStartup("fixture", false),
            "read" => workspace.Read(input["language"]?.GetValue<string>() ?? "zh-CN"),
            _ => workspace.Patch(input["values"]!.AsObject()),
        };
    }
    catch (Exception error) when (error is ArgumentException or ConfigWorkspaceException)
    { result["error"] = error.Message; }
    catch (Exception error) { throw new Exception($"fixture case {index - 1}; HRESULT={error.HResult:X8}", error); }
    result["schema"] = workspace.Read();
    result["unchanged"] = before == File.ReadAllText(file);
    result["stored"] = File.ReadAllText(file);
    output.Add(result);
}
// Independent workspaces share the file lock, merging the latest state.
string shared = Path.Combine(args[1], "concurrent");
Directory.CreateDirectory(Path.Combine(shared, "config"));
File.WriteAllText(Path.Combine(shared, "config", "fixture.json"), "{\"Alas\":{}}");
File.WriteAllText(Path.Combine(shared, "config", "template.json"), "{\"Alas\":{}}");
var writers = Enumerable.Range(0, 8).Select(_ => new DeploySettingsWorkspace(shared)).ToArray();
if (File.Exists(Path.Combine(shared, "config", "deploy.yaml"))) throw new Exception("constructor wrote configuration");
writers[0].Read();
if (File.Exists(Path.Combine(shared, "config", "deploy.yaml"))) throw new Exception("read wrote configuration");
await Task.WhenAll(writers.Select((writer, i) => Task.Run(() => writer.Patch(new JsonObject { [new[] {
    "Branch", "GitExecutable", "PythonExecutable", "AdbExecutable", "OcrClientAddress", "SSHServer", "WebuiHost", "WebuiSSLKey"
}[i]] = "fixture-" + i }))));
string stored = File.ReadAllText(Path.Combine(shared, "config", "deploy.yaml"));
for (int i = 0; i < 8; i++) if (!stored.Contains("fixture-" + i)) throw new Exception("concurrent field lost");
writers[0].SetStartup("fixture", true);
writers[1].Patch(new JsonObject { ["Branch"] = "after-startup" });
if (!writers[0].ReadStartup("fixture")["enabled"]!.GetValue<bool>()) throw new Exception("ordinary save cleared startup");
if (OperatingSystem.IsWindows())
{
    string target = Path.Combine(shared, "config", "deploy.yaml");
    // An external reader without delete sharing must not cause a torn write.
    using (var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var delayed = Task.Run(() => writers[0].Patch(new JsonObject { ["Branch"] = "after-reader" }));
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (!Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "deploy.yaml.*.tmp").Any() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        if (delayed.IsCompleted) throw new Exception("replacement should wait for denied delete sharing");
        reader.Dispose();
        await delayed;
    }
    string before = File.ReadAllText(target);
    using (var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool failed = false;
        try { writers[0].Patch(new JsonObject { ["Branch"] = "must-not-save" }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        if (!failed || File.ReadAllText(target) != before) throw new Exception("denied replacement must fail without changing bytes");
    }
    if (Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "deploy.yaml.*.tmp").Any()) throw new Exception("temporary file leaked");
}
Console.WriteLine(output.ToJsonString());
if (args.Length > 2)
{
    using var client = new ControlClient(new Uri(args[2]));
    await client.GetStateAsync();
    var schema = await client.GetDeploySettingsAsync();
    if (schema.Groups.Count != 8) throw new Exception("client schema lost groups");
    var saved = await client.PatchDeploySettingsAsync(new DeploySettingsPatchRequest { Values = new JsonObject { ["WebuiPort"] = "23456" } });
    if (!saved.Updated.SequenceEqual(new[] { "WebuiPort" })) throw new Exception("client write response");
    var startup = await client.SetStartupRunAsync(new StartupRunRequest { Instance = "fixture", Enabled = true });
    if (!startup.Enabled || !(await client.GetStartupRunAsync("fixture")).Enabled) throw new Exception("client startup");
}
'''


def check_cases(source, fixtures, actual, work):
    native = reference(source)
    definition = export(source)
    defaults = definition['defaults']['windows' if sys.platform == 'win32' else 'unix']
    translator = lambda key: definition['translations']['zh-CN'].get(key.rsplit('.', 1)[-1], key)
    for i, (item, result) in enumerate(zip(fixtures, actual, strict=True)):
        path = work / 'reference.yaml'
        path.write_text(item['text'], encoding='utf-8')
        initial = defaults | native['poor_yaml_read'](path)
        values = copy.deepcopy(initial)
        error = bool(item.get('demo')) and item.get('operation') not in ('read', 'startup')
        updated = []
        if item.get('operation', 'patch') == 'patch':
            for key, value in item['values'].items():
                if key == 'Run' or (key == 'Password' and value == ''):
                    continue
                try:
                    values[key] = native['_parse_value'](native['DEPLOY_FIELDS'][key], value)
                    updated.append(key)
                except (ValueError, TypeError, KeyError):
                    error = True
            error |= item.get('must_reject', False)
            if error:
                assert result.get('error') and result['unchanged'], (i, 'batch must reject atomically', result)
                values = initial
            else:
                assert result.get('result') == {'updated': sorted(updated)}, (i, result)
                # Use the original upstream reader on both persisted candidates.
                text = '\n'.join(f"{key}: {'null' if value is None else 'true' if value is True else 'false' if value is False else str(value)}" for key, value in values.items())
                path.write_text(text, encoding='utf-8')
                values = defaults | native['poor_yaml_read'](path)
        elif item['operation'] in ('startup', 'enable', 'disable'):
            runs = native['parse_run_config'](values.get('Run'))
            if item['operation'] == 'enable' and 'fixture' not in runs:
                runs.append('fixture')
            if item['operation'] == 'disable':
                runs = [name for name in runs if name != 'fixture']
            if item['operation'] != 'startup':
                values['Run'] = native['format_run_config'](runs)
            expected = {'instance': 'fixture', 'enabled': 'fixture' in runs, 'run': runs, 'raw': values.get('Run')}
            assert result.get('result') == expected, (i, result.get('result'), expected)
        native['State'] = SimpleNamespace(deploy_config=SimpleNamespace(read=lambda: None, config=values))
        native['is_demo_mode'] = lambda: bool(item.get('demo'))
        expected = native['deploy_settings_schema'](translator)
        for group in expected['groups']:
            for field in group['fields']:
                if field['key'] == 'Password':
                    field.update(value='', type='password')
        assert result['schema'] == expected, (i, item, result['schema'], expected)
        if item.get('must_reject') or error:
            continue
        path.write_text(result['stored'], encoding='utf-8')
        persisted = defaults | native['poor_yaml_read'](path)
        assert persisted == values, (i, 'persisted differs', persisted, values)
    return len(fixtures)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--upstream', type=Path, default=ROOT.parent / 'others fork version/AzurPilot')
    args = parser.parse_args()
    definition = export(args.upstream)
    assert json.loads((ROOT / 'src/Alas.Core/Runtime/Resources/deploy-settings.json').read_text(encoding='utf-8')) == definition
    fields = reference(args.upstream)['DEPLOY_FIELDS']
    fixtures = [{'operation': 'read', 'text': ''}, {'operation': 'read', 'text': 'Password: fixture-secret\nBranch: untouched\n'}]
    for key, field in fields.items():
        candidates = {'bool': [True, False, 1, 'true', None],
                      'int': [0, ' ２_３ ', '+0008', 2.9, -0.8, -1, '2.1', False, None, 2**80],
                      'select': [*field.options, 'unknown', None],
                      'string': [' fixture ', '', None, False, 12, 2.5, 1e20, 1e-7, 1e-5, 1e16, 1000.0, -0.0, ['one', None], {'a': True}],
                      'nullable_string': ['fixture', '', None, False],
                      'cdn': ['', None, False, True, 'NULL', ' TRUE ', 'fixture']}[field.kind]
        for value in candidates:
            fixtures.append({'text': 'Password: fixture-secret\nRun: ["existing"]\nUnknownFuture: retained\n', 'values': {key: value}})
    fixtures += [{'text': 'Branch: untouched\n', 'values': {'Branch': 'changed', 'Unknown': 1}},
                 {'text': 'Branch: untouched\n', 'values': {'Branch': 'changed', 'WebuiPort': True}},
                 {'text': 'Password: fixture-secret\n', 'values': {'Password': ''}},
                 {'text': 'Run: ["fixture"]\n', 'values': {'Run': ['ignored'], 'Branch': 'changed'}},
                 {'text': 'Branch: untouched\n', 'values': {'Branch': 'changed'}, 'demo': True}]
    for injection in ['a\nWebuiPort: 9', 'a\rWebuiPort: 9', 'a\u2028WebuiPort: 9', 'a\0b']:
        fixtures.append({'text': 'Branch: untouched\n', 'values': {'Branch': injection}, 'must_reject': True})
    for raw in ['null', 'false', "['one', 'fixture', 'one']", '["fixture","测试"]', 'fixture, one, fixture', '[true,null,3]', '  ', '0012']:
        for operation in ['startup', 'enable', 'disable']:
            fixtures.append({'text': 'Run: ' + raw + '\n', 'operation': operation})
    local = ROOT / '.runtime/verification'
    local.mkdir(parents=True, exist_ok=True)
    dotnet = dotnet_env.executable(ROOT)
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(ROOT / '.runtime/dotnet-home'),
               NUGET_PACKAGES=str(ROOT / '.runtime/nuget/packages'))
    env.pop('DEMO', None)
    with tempfile.TemporaryDirectory(prefix='deploy-settings-', dir=local) as directory:
        work = Path(directory)
        (work / 'fixtures.json').write_text(json.dumps(fixtures, ensure_ascii=False), encoding='utf-8')
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        project = work / 'DeployProbe.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" /><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Client/Alas.Client.csproj'))}" /></ItemGroup></Project>''', encoding='utf-8')
        build = subprocess.run([str(dotnet), 'build', str(project), '-c', 'Release', *(['--source', str(ROOT / '.runtime/nuget/source')]
                                   if (ROOT / '.runtime/nuget/source').is_dir() else []), '-p:NuGetAudit=false'], env=env, capture_output=True, timeout=120)
        assert build.returncode == 0, (build.stdout + build.stderr).decode(errors='replace')
        repo = work / 'api-repo'
        (repo / 'config').mkdir(parents=True)
        for name in ('fixture', 'template'):
            (repo / 'config' / f'{name}.json').write_text('{"Alas":{}}', encoding='utf-8')
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        process = subprocess.Popen([str(dotnet), str(ROOT / 'src/Alas.Server/bin/Release/net10.0/Alas.Server.dll'),
                                    '--root', str(work), '--repo', str(repo), '--data', str(work / 'data'),
                                    '--tools', str(work / 'tools'), '--workspace', str(work / 'workspace'), '--port', str(port)],
                                   env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, cwd=ROOT)
        try:
            for _ in range(60):
                try:
                    status, state = request(base, '/api/state')
                    if status == 200:
                        break
                except OSError:
                    time.sleep(.1)
            else:
                raise AssertionError('fixture server did not start')
            token = state['token']
            assert request(base, '/api/settings', 'PATCH', {'values': {'Branch': 'test'}})[0] == 403
            assert request(base, '/api/settings', 'PATCH', {'values': []}, token)[0] == 400
            assert request(base, '/api/startup', 'POST', {'instance': 'fixture', 'enabled': 'true'}, token)[0] == 400
            assert request(base, '/api/startup', 'POST', {'instance': 'missing', 'enabled': True}, token)[0] == 404
            for invalid in ('../escape', 'bad.name', 'template', 'CON'):
                assert request(base, '/api/startup', 'POST', {'instance': invalid, 'enabled': True}, token)[0] == 400
            assert request(base, '/api/settings?language=invalid')[0] == 400
            assert not (repo / 'config/deploy.yaml').exists()
            result = subprocess.run([str(dotnet), str(work / 'bin/Release/net10.0/DeployProbe.dll'), str(work / 'fixtures.json'), str(work / 'cases'), base], env=env, capture_output=True, timeout=120)
            assert result.returncode == 0, result.stderr.decode(errors='replace')
            count = check_cases(args.upstream, fixtures, json.loads(result.stdout), work)
            schema = request(base, '/api/settings')[1]
            assert next(field['value'] for group in schema['groups'] for field in group['fields'] if field['key'] == 'WebuiPort') == 23456
            assert request(base, '/api/startup?instance=fixture')[1]['enabled']
            for language in definition['translations']:
                assert request(base, '/api/settings?language=' + language)[0] == 200
        finally:
            process.terminate()
            process.wait(timeout=15)
    print(f'PASS: {count} original-upstream deployment cases; atomic rejection, concurrent merge, password/startup, typed client and real Kestrel.')


if __name__ == '__main__':
    main()
