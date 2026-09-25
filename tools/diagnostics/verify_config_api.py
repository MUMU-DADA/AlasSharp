#!/usr/bin/env python3
"""Offline contract check for the server-backed upstream configuration API."""
from __future__ import annotations

import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def request(base: str, path: str, method: str = "GET", body: dict | None = None,
            token: str | None = None) -> tuple[int, dict]:
    raw = None if body is None else json.dumps(body, ensure_ascii=False).encode()
    headers = {"Content-Type": "application/json"}
    if token:
        headers["X-Alas-Token"] = token
    req = urllib.request.Request(base + path, data=raw, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=5) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


def main() -> int:
    if not EXE.is_file():
        raise SystemExit("先构建 src/Alas.Server/Alas.Server.csproj")
    local = ROOT / ".runtime/config-api-test"
    local.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="alas-config-api-", dir=local) as folder:
        root = Path(folder)
        repo = root / "repo"
        (repo / 'config').mkdir(parents=True)
        template = {'Alas': {'Emulator': {'PackageName': 'com.bilibili.azurlane', 'Serial': 'fixture-device', 'ServerName': 'cn'}}}
        template['Reward'] = {'Scheduler': {'Enable': True, 'NextRun': '2000-01-01 00:00:00'}}
        template['Research'] = {'Scheduler': {'Enable': True, 'NextRun': '2999-01-01 00:00:00'}}
        template['Dashboard'] = {'Oil': {'Value': 1234, 'Limit': 25000, 'Record': '2026-01-01 00:00:00'}}
        for name in ('template', 'alas'):
            (repo / 'config' / (name + '.json')).write_text(json.dumps(template), encoding='utf-8')
        (repo / 'config/not-an-instance.json').write_text('{}', encoding='utf-8')
        shutil.copytree(ROOT / ".runtime" / "engine" / "module" / "config", repo / "module" / "config", symlinks=True)
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        process = subprocess.Popen(
            [str(EXE), "--root", str(root), "--repo", str(repo),
             "--data", "data", "--tools", "tools", "--workspace", str(root / "workspace"),
             "--port", str(port)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            cwd=ROOT)
        base = f"http://127.0.0.1:{port}"
        try:
            for _ in range(60):
                try:
                    status, state = request(base, "/api/state")
                    if status == 200:
                        break
                except Exception:
                    time.sleep(.1)
            else:
                raise AssertionError("控制服务未启动")
            token = state["token"]
            status, listing = request(base, "/api/instances")
            assert status == 200 and [i['instance'] for i in listing['instances']] == ['alas']
            assert listing['instances'][0]['server'] == 'cn'
            assert listing['instances'][0]['status'] == 'stopped'
            status, instance_state = request(base, '/api/state?instance=alas')
            assert status == 200 and instance_state['recent_logs'] == []
            overview = instance_state['overview']
            assert overview['source'] == 'configuration' and overview['instance'] == 'alas'
            assert [item['name'] for item in overview['pending']] == ['Reward']
            assert [item['name'] for item in overview['waiting']] == ['Research']
            assert overview['resources'][0]['value'] == 1234
            assert request(base, '/api/state?instance=missing')[0] == 404
            assert request(base, '/api/state?instance=..%2Fescape')[0] == 400
            schema_status, schema = request(base, "/api/schema?language=zh-CN")
            assert schema_status == 200 and {"menu", "args", "translations"} <= schema.keys()
            get_status, config = request(base, "/api/config/alas")
            assert get_status == 200 and config["instance"] == "alas" and len(config["revision"]) == 64
            path = "Alas.Emulator.PackageName"
            old = config["values"]["Alas"]["Emulator"]["PackageName"]
            assert request(base, "/api/config/alas", "PATCH", {
                "revision": config["revision"], "changes": [{"path": path, "value": old}],
            })[0] == 403
            patch_status, patched = request(base, "/api/config/alas", "PATCH", {
                "revision": config["revision"], "changes": [{"path": path, "value": old}],
            }, token)
            assert patch_status == 200 and patched["values"]["Alas"]["Emulator"]["PackageName"] == old
            bad_status, _ = request(base, "/api/config/alas", "PATCH", {
                "changes": [{"path": path, "value": 1}],
            }, token)
            assert bad_status >= 400
            sys.path.insert(0, str(ROOT / '.runtime' / 'engine'))
            from module.api.config_service import ConfigService
            from module.api.protocol import ApiError
            native = ConfigService(repo)
            validation_cases = [
                ('Restart.Scheduler.NextRun', '2026-02-28 09:10:11', True),
                ('Restart.Scheduler.NextRun', '2026-02-30 09:10:11', False),
                ('Restart.Scheduler.NextRun', '2026-2-28 09:10:11', False),
                ('Main.StopCondition.RunCount', 2, True),
                ('Main.StopCondition.RunCount', 2.5, False),
                ('Alas.Optimization.ScreenshotInterval', 0.3, True),
                ('Alas.Optimization.ScreenshotInterval', '0.3', False),
                ('Main.Storage.Storage', {}, True),
                ('Main.Storage.Storage', {'unexpected': 1}, False),
                ('Alas.Error.OnePushConfig', 'provider: null', True),
                ('Alas.Error.OnePushConfig', '', True),
                ('Alas.Error.OnePushConfig', 'null', True),
                ('Alas.Error.OnePushConfig', '[]', False),
                ('Alas.Error.OnePushConfig', 'provider: [1, 2', False),
                ('Alas.Error.OnePushConfig', '---\na: 1\n---\nb: 2', False),
            ]
            instance_file = repo / 'config' / 'alas.json'
            for field, value, accepted in validation_cases:
                try:
                    native.validate(field, value)
                    native_accepted = True
                except ApiError:
                    native_accepted = False
                assert native_accepted == accepted, (field, value, native_accepted)
                before = instance_file.read_bytes()
                status, result = request(base, '/api/config/alas', 'PATCH', {
                    'changes': [{'path': field, 'value': value}],
                }, token)
                assert (status == 200) == native_accepted, (field, value, status, result)
                if not accepted:
                    assert instance_file.read_bytes() == before, field
            mixed_status, mixed_result = request(base, '/api/config/alas', 'PATCH', {
                'changes': [{'path': 'IslandProductionPlanner.IslandProductionPlanner.FieldsEfficiency',
                             'value': 0.04}],
            }, token)
            assert mixed_status == 200, mixed_result
            assert mixed_result['values']['IslandProductionPlanner']['IslandProductionPlanner']['FieldsEfficiency'] == 0.04
            args_file = repo / 'module/config/argument/args.json'
            synthetic_args = json.loads(args_file.read_text(encoding='utf-8'))
            synthetic_args['Alas']['Optimization'].update({
                'SyntheticRange': {'type': 'input', 'value': 0, 'validate': [1, 5]},
                'SyntheticPattern': {'type': 'input', 'value': '', 'validate': '[A-Z]{2}[0-9]{2}'},
                'SyntheticMulti': {'type': 'multiselect', 'value': [], 'option': ['1', 1, True]},
            })
            args_file.write_text(json.dumps(synthetic_args), encoding='utf-8')
            native = ConfigService(repo)
            for field, value, accepted in [
                ('Alas.Optimization.SyntheticRange', 3, True),
                ('Alas.Optimization.SyntheticRange', 6, False),
                ('Alas.Optimization.SyntheticRange', 3.5, False),
                ('Alas.Optimization.SyntheticPattern', 'AB12', True),
                ('Alas.Optimization.SyntheticPattern', 'ab12', False),
                ('Alas.Optimization.SyntheticMulti', [1, True], True),
                ('Alas.Optimization.SyntheticMulti', ['1', 1], False),
                ('Alas.Optimization.SyntheticMulti', [1, 1], False),
            ]:
                try:
                    native.validate(field, value)
                    native_accepted = True
                except ApiError:
                    native_accepted = False
                assert native_accepted == accepted, (field, value, native_accepted)
                before = instance_file.read_bytes()
                status, result = request(base, '/api/config/alas', 'PATCH', {
                    'changes': [{'path': field, 'value': value}],
                }, token)
                assert (status == 200) == native_accepted, (field, value, status, result)
                if not accepted:
                    assert instance_file.read_bytes() == before, field
            assert request(base, '/api/instances/importable')[1] == {'sources': []}
            upload = {'name': 'import-fixture', 'content': json.dumps(template)}
            assert request(base, '/api/instances/import', 'POST', upload)[0] == 403
            for bad in ('../escape', 'template', 'CON', 'x/y', 'x\\y'):
                assert request(base, '/api/instances/import', 'POST', {**upload, 'name': bad}, token)[0] == 400
            for bad in ('{', '[]', '{}', '{"Alas":1}'):
                assert request(base, '/api/instances/import', 'POST', {**upload, 'content': bad}, token)[0] == 400
            assert not (repo / 'config/import').exists(), 'invalid imports must not create files'
            status, staged = request(base, '/api/instances/import', 'POST', upload, token)
            assert status == 200 and staged['name'] == 'import-fixture'
            assert request(base, '/api/instances/importable')[1]['sources'][0]['name'] == 'import-fixture'
            assert [i['instance'] for i in request(base, '/api/instances')[1]['instances']] == ['alas'], 'staged import is not an instance'
            status, created = request(base, '/api/instances', 'POST', {
                'instance': '  fixture-created  ', 'import_file': 'import-fixture'}, token)
            assert status == 201 and created['instance'] == 'fixture-created'
            assert created['values']['Alas']['Emulator'] == template['Alas']['Emulator']
            assert request(base, '/api/instances', 'POST', {'instance': 'fixture-created'}, token)[0] == 400
            imported_path = repo / 'config/import/import-fixture.json'
            before = imported_path.read_bytes()
            assert request(base, '/api/instances/import', 'POST', {**upload, 'content': '{}'}, token)[0] == 400
            assert imported_path.read_bytes() == before, 'failed replacement must preserve staged config'
            (repo / 'config/import/invalid.json').write_text('not json', encoding='utf-8')
            assert len(request(base, '/api/instances/importable')[1]['sources']) == 1
            assert request(base, '/api/instances/fixture-created', 'DELETE', {'revision': 'stale'}, token)[0] == 409
            assert request(base, '/api/instances/fixture-created', 'DELETE', {'revision': created['revision']}, token)[0] == 200
            backups = list((repo / 'config/backup').glob('fixture-created-*.json'))
            assert len(backups) == 1 and json.loads(backups[0].read_text())['Alas'] == template['Alas']
            assert not (repo / 'config/fixture-created.json').exists()
            assert (repo / 'config/alas.json').is_file(), 'another instance is preserved'
            print("PASS: native descriptor/date/numeric/storage/YAML patch parity and mixed options, rejected writes unchanged, import/create/delete; no accounts or device")
            return 0
        finally:
            process.terminate()
            process.wait(timeout=10)


if __name__ == "__main__":
    raise SystemExit(main())
