"""Verify original upstream DeployConfig with Core on synthetic files only."""
from __future__ import annotations

import ast
import copy
from functools import cached_property
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
from typing import Optional, Union
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from deploy_storage import deploy_transaction, install_on


HARNESS = r'''
using System.Text.Json.Nodes;
using Alas.Runtime;
if (args[0] == "hold")
{
    using var stream = new FileStream(Path.Combine(args[1], "config", "deploy.yaml.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Console.WriteLine("LOCKED"); Console.Out.Flush();
    Console.ReadLine();
}
else
{
    var workspace = new DeploySettingsWorkspace(args[1]);
    Console.WriteLine(workspace.Patch(JsonNode.Parse(args[2])!.AsObject()).ToJsonString());
}
'''


def original(source, root):
    template = root / 'template'
    template.write_text('Branch: master\nPassword: null\nRun: null\nSSLVerify: true\n', encoding='utf-8')
    writes = []

    def read(file):
        return Path(file).read_text(encoding='utf-8') if Path(file).exists() else ''

    def write(file, text):
        writes.append(text)
        temp = Path(str(file) + '.test.tmp')
        temp.write_text(text, encoding='utf-8')
        temp.replace(file)

    namespace = {'os': os, 'sys': sys, 'copy': copy, 're': re, 'Optional': Optional, 'Union': Union,
                 'cached_property': cached_property, 'DEPLOY_TEMPLATE': str(template),
                 'DEPLOY_CONFIG': str(root / 'config/deploy.yaml'), 'get_deploy_template': lambda: str(template),
                 'get_country_code': lambda: None, 'atomic_read_text': read, 'atomic_write': write,
                 'logger': SimpleNamespace(hr=lambda *a: None, info=lambda *a: None, warning=lambda *a: None)}
    tree = ast.parse((source / 'deploy/utils.py').read_text(encoding='utf-8'))
    nodes = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in ('poor_yaml_read', 'poor_yaml_write')]
    exec(compile(ast.Module(nodes, []), '<upstream-deploy-utils>', 'exec'), namespace)
    tree = ast.parse((source / 'deploy/config.py').read_text(encoding='utf-8'))
    nodes = [n for n in tree.body if isinstance(n, (ast.ClassDef, ast.Assign))]
    exec(compile(ast.Module(nodes, []), '<upstream-deploy-config>', 'exec'), namespace)
    base = namespace['DeployConfig']
    subclass_path = source / 'module/runtime/config.py'
    if not subclass_path.exists():
        subclass_path = source / 'module/webui/config.py'
    tree = ast.parse(subclass_path.read_text(encoding='utf-8'))
    subclass_namespace = {'_DeployConfig': base}
    exec(compile(ast.Module([n for n in tree.body if isinstance(n, ast.ClassDef)], []), '<upstream-autosave-config>', 'exec'), subclass_namespace)
    return base, subclass_namespace['DeployConfig'], SimpleNamespace(**namespace), writes


def main():
    local = ROOT / '.runtime/verification'
    local.mkdir(parents=True, exist_ok=True)
    dotnet = ROOT / '.runtime/dotnet/dotnet.exe'
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(ROOT / '.runtime/dotnet-home'),
               NUGET_PACKAGES=str(ROOT / '.runtime/nuget/packages'))
    env.pop('DEMO', None)
    with tempfile.TemporaryDirectory(prefix='deploy-storage-', dir=local) as directory:
        work = Path(directory)
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        project = work / 'StorageProbe.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" /></ItemGroup></Project>''', encoding='utf-8')
        build = subprocess.run([str(dotnet), 'build', str(project), '-c', 'Release', '--source', str(ROOT / '.runtime/nuget/source'), '-p:NuGetAudit=false'], env=env, capture_output=True, timeout=120)
        assert build.returncode == 0, (build.stdout + build.stderr).decode(errors='replace')
        dll = work / 'bin/Release/net10.0/StorageProbe.dll'

        def start(mode, root, data=None):
            command = [str(dotnet), str(dll), mode, str(root)]
            if data is not None:
                command.append(json.dumps(data))
            return subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)

        def patch(root, data):
            process = start('patch', root, data)
            stdout, stderr = process.communicate(timeout=20)
            assert process.returncode == 0, stderr.decode(errors='replace')
            return json.loads(stdout)

        for label, source in [('original', ROOT / '.runtime/engine'), ('fork', ROOT.parent / 'others fork version/AzurPilot')]:
            root = work / label
            (root / 'config').mkdir(parents=True)
            file = root / 'config/deploy.yaml'
            fixture = 'Branch: master\nPassword: fixture-secret\nRun: ["fixture"]\nRemoteAccessMode: webrtc\nUnknownFuture: keep\n'
            file.write_text(fixture, encoding='utf-8')
            base, autosave, utils, writes = original(source, root)
            # Reproduce the real data loss with the unmodified original writer.
            baseline = autosave(file=str(file))
            baseline.Branch = 'old-write'
            assert 'RemoteAccessMode' not in utils.poor_yaml_read(file)
            file.write_text('# retained comment\n' + fixture, encoding='utf-8')
            install_on(base, utils)
            read_method = base.read
            install_on(base, utils)
            assert base.read is read_method, 'installation must be idempotent'
            config = autosave(file=str(file))
            assert utils.poor_yaml_read(file)['RemoteAccessMode'] == 'webrtc'
            patch(root, {'RemoteAccessMode': 'ssh', 'MaxRedirects': 8, 'Password': 'fixture-updated'})
            config.Branch = 'host-write'
            values = utils.poor_yaml_read(file)
            assert values['Branch'] == 'host-write' and values['Password'] == 'fixture-updated'
            assert values['RemoteAccessMode'] == 'ssh' and values['MaxRedirects'] == 8
            assert values['Run'] == '["fixture"]' and values['UnknownFuture'] == 'keep'
            assert '# retained comment' in file.read_text(encoding='utf-8')
            config.read()
            patch(root, {'Branch': 'newer-core'})
            before = file.read_bytes()
            try:
                config.Branch = 'stale-host'
            except RuntimeError as error:
                assert '重新读取' in str(error)
            else:
                raise AssertionError('same-field stale host overwrite was accepted')
            assert file.read_bytes() == before
            config.read()
            config.Branch = 'after-refresh'
            config.config['GitProxy'] = 'fixture\nPassword: overwritten'
            before = file.read_bytes()
            try:
                config.write()
            except ValueError:
                pass
            else:
                raise AssertionError('newline injection accepted')
            assert file.read_bytes() == before
            config.read()

            # Core must block while Python holds the OS-level transaction.
            with deploy_transaction(file):
                process = start('patch', root, {'Branch': 'after-python-lock'})
                time.sleep(.15)
                assert process.poll() is None
            stdout, stderr = process.communicate(timeout=20)
            assert process.returncode == 0, stderr.decode(errors='replace')
            assert utils.poor_yaml_read(file)['Branch'] == 'after-python-lock'
            # Python must time out without writing while Core holds it.
            holder = start('hold', root)
            try:
                assert holder.stdout.readline().strip() == b'LOCKED'
                before = file.read_bytes()
                try:
                    with deploy_transaction(file, timeout=.1):
                        raise AssertionError('Python ignored the Core lock')
                except OSError:
                    pass
                assert file.read_bytes() == before
            finally:
                holder.communicate(b'\n', timeout=10)
            config.read()
            config.Branch = 'after-lock-release'
            assert utils.poor_yaml_read(file)['Branch'] == 'after-lock-release'

            # A failed atomic write leaves both file and retry baseline intact.
            before = file.read_bytes()
            snapshot = copy.deepcopy(config._alas_deploy_snapshot)
            config.config['Branch'] = 'retry-after-failure'
            original_write = utils.atomic_write
            utils.atomic_write = lambda *a: (_ for _ in ()).throw(OSError('fixture write failure'))
            try:
                try:
                    config.write()
                except OSError:
                    pass
                else:
                    raise AssertionError('failed writer returned success')
            finally:
                utils.atomic_write = original_write
            assert file.read_bytes() == before and config._alas_deploy_snapshot == snapshot
            config.write()
            assert utils.poor_yaml_read(file)['Branch'] == 'retry-after-failure'

            # Preserve the original repository/mirror redirect and its effective
            # attribute, including the fork's removal of legacy AutoUpdate.
            redirected = root / 'config/redirect.yaml'
            redirected.write_text('Repository: https://gitee.com/LmeSzinc/AzurLaneAutoScript\n'
                                  'PypiMirror: https://pypi.tuna.tsinghua.edu.cn/simple\nAutoUpdate: true\n', encoding='utf-8')
            redirected_config = autosave(file=str(redirected))
            saved = utils.poor_yaml_read(redirected)
            assert saved['PypiMirror'] == 'https://mirrors.aliyun.com/pypi/simple'
            expected_repo = 'git://git.pull/AzurPilot' if label == 'fork' else 'git://git.lyoko.io/AzurLaneAutoScript'
            assert saved['Repository'] == expected_repo and redirected_config.GitOverCdn
            if label == 'fork':
                assert 'AutoUpdate' not in saved
            print(f'PASS: {label} original DeployConfig/subclass preserves Core fields, password, Run and redirects; stale conflict and bidirectional locks.')

    tree = ast.parse((ROOT / 'tools/alas_vision.py').read_text(encoding='utf-8'))
    handle = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'handle')
    code = ast.unparse(handle)
    assert code.index('install_deploy_storage()') < code.index("fn(req.get('args')")
    print('PASS: installed before native operation dispatch; no device, production deployment file or visible UI used.')


if __name__ == '__main__':
    main()
