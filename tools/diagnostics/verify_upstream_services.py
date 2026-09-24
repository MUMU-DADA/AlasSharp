#!/usr/bin/env python3
"""Exercise cached upstream services in a synthetic, isolated repository.

Real service code and databases, synthetic records. No account configuration,
hardware identity collection, network requests, or device calls are used.
"""
from __future__ import annotations

import ast
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from cache_upstream_services import FILES, ENGINE_DEPENDENCIES, digest


def worker(repo: Path):
    os.chdir(repo)
    sys.path.insert(0, str(repo))
    # These two hardware-related functions alone are fixture dependencies.
    # All report services, storage and serializers below are real upstream code.
    import module.base.device_id as identity
    identity.generate_device_id = lambda: 'offline-service-fixture'
    identity._start_refresh_timer = lambda *args: None
    names = {'_api_config_service', 'op_statistics_report', 'op_statistics_refresh_loot',
             'op_meowfficer_report', 'op_meowfficer_clear', 'op_shop_strategy_validate',
             '_require_loot_statistics'}
    tree = ast.parse((ROOT / 'tools/alas_vision.py').read_text(encoding='utf-8'))
    selected = ast.Module([n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in names], [])
    namespace = {'FORK': str(repo), 'Path': Path}
    exec(compile(selected, '<host-services>', 'exec'), namespace)
    for relative in ('module/config/argument/args.json', 'module/config/argument/menu.json',
                     'module/config/i18n/zh-CN.json', 'config/template.json',
                     'config/fixture.json', 'config/second.json'):
        p = repo / relative
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text('{"Alas":{}}', encoding='utf-8')

    validate = namespace['op_shop_strategy_validate']
    assert validate({'script': 'return shop.plan { candidates = candidates:take(1) }'}) == {
        'valid': True, 'diagnostics': []}
    invalid = validate({'script': 'os.execute("forbidden")'})
    assert invalid['valid'] is False and invalid['diagnostics'][0]['code']
    assert invalid['diagnostics'][0]['line'] == 1 and invalid['diagnostics'][0]['column'] >= 1

    from module.statistics.resource_stats import record_resource_snapshot
    record_resource_snapshot('fixture', {'Oil': 123, 'Coin': 456})
    record_resource_snapshot('second', {'Oil': 999})
    report = namespace['op_statistics_report']
    resource = report({'instance': 'fixture', 'category': 'resources'})
    assert next(s for s in resource['series'] if s['key'] == 'oil')['points'][0]['value'] == 123
    assert all(p['value'] != 999 for s in resource['series'] for p in s['points'])
    assert not next(s for s in resource['series'] if s['key'] == 'gem')['points']
    for category in ('action', 'opsi', 'commission', 'ships'):
        value = report({'instance': 'fixture', 'category': category, 'month': datetime.now().strftime('%Y-%m')})
        assert value['instance'] == 'fixture' and value['category'] == category
        assert {'metrics', 'series', 'tables', 'notes'} <= value.keys()
    for params in ({'instance': '../fixture'}, {'instance': 'missing'},
                   {'instance': 'fixture', 'category': 'unknown'},
                   {'instance': 'fixture', 'days': 400}):
        try:
            report(params)
        except Exception:
            pass
        else:
            raise AssertionError('invalid report input was accepted')

    cats = [{'cat': 'fixture-cat', 'tags': ['SSR'], 'pointsSpent': 5,
             'talents': [{'name': 'fixture-talent', 'level': 2, 'kind': 'special', 'inferred': True}],
             'rubrics': [{'label': 'fixture-rubric', 'key': 'fleet', 'xHits': ['a'], 'primary': True}],
             'advice': {'verdict': 'keep', 'headline': 'fixture', 'reason': 'fixture',
                        'costText': 'fixture cost', 'costEstimated': True, 'targets': ['target']}}]
    payload = {'generatedAt': '2026-01-01 00:00:00', 'count': 1, 'cats': cats}
    path = repo / 'log/meowfficer_score.json'
    path.parent.mkdir(exist_ok=True)
    path.write_text(json.dumps(payload), encoding='utf-8')
    value = namespace['op_meowfficer_report']({'instance': 'fixture'})
    assert value['cats'] == cats and value['generatedAt'] == payload['generatedAt'] and value['count'] == 1
    for suffix in ('.md', '.html'):
        path.with_suffix(suffix).write_text('fixture', encoding='utf-8')
    cleared = namespace['op_meowfficer_clear']({'instance': 'fixture'})
    assert cleared['cleared'] is True and len(cleared['removed']) == 3
    assert namespace['op_meowfficer_clear']({'instance': 'fixture'})['cleared'] is False
    try:
        namespace['op_meowfficer_report']({'instance': 'fixture'})
    except Exception as error:
        assert error.code == 'NOT_FOUND'
    else:
        raise AssertionError('missing report must not become a success')

    import types
    unavailable = types.ModuleType('module.statistics.azurstats')
    unavailable.AzurStats = type('AzurStats', (), {})
    with patch.dict(sys.modules, {'module.statistics.azurstats': unavailable}):
        for name in ('op_statistics_report', 'op_statistics_refresh_loot'):
            try:
                namespace[name]({'instance': 'fixture', 'category': 'loot'})
            except RuntimeError as error:
                assert '掉落' in str(error)
            else:
                raise AssertionError('missing loot capability must fail explicitly')
    print('PASS: five statistics categories, instance isolation, nested score report/clear, Lua diagnostics, loot capability boundary')


def main():
    if len(sys.argv) > 1 and sys.argv[1] == '--worker':
        worker(Path(sys.argv[2]))
        return 0
    engine = ROOT / '.runtime/engine'
    receipt = json.loads((engine / 'upstream-services.manifest.json').read_text(encoding='utf-8'))
    assert {item['path'] for item in receipt['files']} == set(FILES)
    for entry in receipt['files'] + receipt['engine_dependencies']:
        assert digest((engine / entry['path']).read_bytes()) == entry['sha256'], entry['path']
    with tempfile.TemporaryDirectory(prefix='services-fixture-', dir=ROOT / '.runtime') as tmp:
        repo = Path(tmp)
        for relative in (*FILES, *ENGINE_DEPENDENCIES):
            target = repo / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(engine / relative, target)
        result = subprocess.run([sys.executable, str(Path(__file__).resolve()), '--worker', str(repo)],
                                env={**os.environ, 'AZURPILOT_NTP_DISABLE': '1', 'PYTHONUTF8': '1'},
                                capture_output=True, text=True, encoding='utf-8')
        if result.returncode:
            print(result.stdout)
            print(result.stderr)
        else:
            print(result.stdout.splitlines()[-1])
        return result.returncode


if __name__ == '__main__':
    raise SystemExit(main())
