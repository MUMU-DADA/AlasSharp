# -*- coding: utf-8 -*-
"""第六域验收：周期任务的调度状态（`kind = "task_schedule"`，只读）。

三段：

1. **与独立读数对拍**（真配置）：C# 任务报出来的任务数/启用数/无 Scheduler 数，
   与脚本直接从 `args.json` + `config/alas.json` 数一遍的结果逐项一致 ——
   单侧读数错了不会两边一起错。
2. **边界情形**（构造的假配置走同一条代码路径）：全禁用 / 全启用 / 缺 `Scheduler` 段 /
   配置不存在（应明确报错而不是崩）。
3. **只读保证**：跑完之后**配置文件字节不变**（`sha256` 前后比对）—— 这条比"我们没写配置"的声明更硬。

用法：
    python tools/diagnostics/verify_task_schedule.py
"""

from __future__ import annotations

import hashlib
import copy
import ast
import io
import json
import os
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
EXE = ROOT / 'src' / 'Alas.Server' / 'bin' / 'Release' / 'net10.0' / 'Alas.Server.exe'
ARGS_JSON = ENGINE / 'module' / 'config' / 'argument' / 'args.json'
CONFIG = ENGINE / 'config' / 'alas.json'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def run_task(instance: str, only_enabled: bool = False,
             input_value: dict | None = None) -> dict:
    """跑一次队列任务，返回它的 evidence（或错误信息）。"""
    with tempfile.TemporaryDirectory(prefix='alas-sched-') as tmp:
        task_input = (dict(input_value) if input_value is not None else {
            'only_enabled': only_enabled, 'limit': 200,
            'instance': instance,
        })
        queue_file = Path(tmp) / 'queue.json'
        queue_file.write_text(json.dumps({'tasks': [{
            'id': 'sched', 'kind': 'task_schedule',
            'input': task_input}]}, ensure_ascii=False), encoding='utf-8')
        artifacts = Path(tmp) / 'artifacts'
        proc = subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                               '--artifacts', str(artifacts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        artifact = next(iter(sorted(artifacts.glob('*/task-sched.json'))), None)
        if artifact is None:
            return {'_run_error': f'退出码={proc.returncode} {(proc.stdout or "")[-200:]}'}
        document = json.loads(artifact.read_text(encoding='utf-8'))
        evidence = document.get('evidence') or {}
        evidence['_outcome'] = document.get('outcome')
        evidence['_error_kind'] = document.get('error_kind')
        evidence['_unmet_preconditions'] = document.get('unmet_preconditions') or []
        evidence['_stdout'] = proc.stdout or ''   # CLI 输出也带回来: 好断言 [任务证据] 行确实打出来了
        evidence['_error'] = document.get('error')
        return evidence


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_host_validation() -> None:
    """Unchanged production operation, in-memory JSON inputs, no host import."""
    tree = ast.parse((ROOT / 'tools/alas_vision.py').read_text(encoding='utf-8'))
    operation = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                     and node.name == 'op_task_schedule')
    payload = {}
    reads = []

    def read(path, **kwargs):
        reads.append(path)
        return io.StringIO(json.dumps({'Alas': {}, 'Main': {}} if str(path).endswith('args.json') else payload))

    environment = dict(json=json, FORK='fixture', open=read,
                       os=SimpleNamespace(path=SimpleNamespace(join=os.path.join)),
                       _instance_config_file=lambda _: ('alas', Path('fixture/config/alas.json'), None))
    exec(compile(ast.Module(body=[operation], type_ignores=[]), 'task_schedule_fixture', 'exec'), environment)
    operation = environment['op_task_schedule']
    invalid_inputs = [dict(only_enabled=value) for value in ('false', 0, 1, None, [], {})]
    invalid_inputs += [dict(limit=value) for value in (0, -1, 1.5, True, '1', None, float('inf'))]
    for arguments in invalid_inputs:
        result = operation(arguments)
        assert result.get('error') and not reads, (arguments, result, reads)
    structures = [(None, '根节点'), ([], '根节点'), ({'Main': None}, 'Main'),
                  ({'Main': {'Scheduler': None}}, 'Main.Scheduler'),
                  ({'Main': {'Scheduler': []}}, 'Main.Scheduler'),
                  ({'Main': {'Scheduler': {'NextRun': 1}}}, 'Main.Scheduler.NextRun'),
                  ({'Alas': {'Scheduler': {'Enable': True}},
                    'Main': {'Scheduler': {'Enable': 'false'}}}, 'Main.Scheduler.Enable')]
    for payload, path in structures:
        result = operation(dict(only_enabled=True, limit=1))
        assert path in result.get('error', '') and 'tasks' not in result, result
    print(f'  OK {len(invalid_inputs) + len(structures)} 宿主输入/结构/截断外非法值拒绝场景')


def verify_response_contract() -> None:
    """Exercise Core's real queue and resume state with inert host responses."""
    base = dict(instance='alas', semantics='stored_config', task_count=1, enabled_count=1,
                no_scheduler_count=0, listed_count=1,
                tasks=[dict(task='Main', enable=True, scheduler_present=True, next_run=None)])
    variants = [('enabled', base, True)]
    for label, flag, present in (('disabled', False, True), ('missing-enable', None, True),
                                ('missing-scheduler', None, False)):
        changed = copy.deepcopy(base)
        changed.update(enabled_count=0, no_scheduler_count=int(not present))
        changed['tasks'][0].update(enable=flag, scheduler_present=present)
        variants.append((label, changed, True))
    for flag in ('false', 0, 1, [], {}):
        changed = copy.deepcopy(base)
        changed['tasks'][0]['enable'] = flag
        variants.append((f'invalid-enable-{type(flag).__name__}-{flag}', changed, False))
    for field in ('enable', 'scheduler_present'):
        changed = copy.deepcopy(base)
        del changed['tasks'][0][field]
        variants.append((f'absent-{field}', changed, False))
    for present in (None, False):
        changed = copy.deepcopy(base)
        changed['tasks'][0]['scheduler_present'] = present
        variants.append((f'contradictory-present-{present}', changed, False))
    for field in ('semantics', 'tasks', 'task_count', 'enabled_count', 'no_scheduler_count', 'listed_count'):
        changed = copy.deepcopy(base)
        del changed[field]
        variants.append((f'absent-{field}', changed, False))
    variants.append(('wrong-instance', {**copy.deepcopy(base), 'instance': 'other'}, False))
    for label, counts in (('row-count-conflict', dict(enabled_count=0)),
                          ('scheduler-count-conflict', dict(enabled_count=0, no_scheduler_count=1)),
                          ('count-overflow', dict(task_count=2147483647, enabled_count=2147483647,
                                                 no_scheduler_count=2147483647, listed_count=2147483647))):
        variants.append((label, {**copy.deepcopy(base), **counts}, False))
    truncated = copy.deepcopy(base)
    truncated.update(task_count=3, listed_count=3, enabled_count=1, no_scheduler_count=1)
    truncated['tasks'][0]['enable'] = False
    variants.append(('truncated-valid', truncated, True))
    # The one visible disabled row leaves two places, which cannot hold three
    # more rows counted as enabled or missing Scheduler.
    variants.append(('truncated-capacity-conflict', {**copy.deepcopy(truncated), 'enabled_count': 2}, False))
    cases = [dict(name=name, mode='queue', dry_run=True, artifacts=True,
                  tasks=[dict(id='snapshot', kind='task_schedule', required=True,
                              input=dict(only_enabled=False, limit=1))],
                  stub_responses={'task_schedule': [dict(result=response)]},
                  expect=dict(outcome='succeeded' if success else 'failed', cleared=False,
                              tasks=[dict(id='snapshot', outcome='succeeded' if success else 'failed')]))
             for name, response, success in variants]
    with tempfile.TemporaryDirectory(prefix='alas-schedule-contract-') as temporary:
        directory = Path(temporary)
        fixture, verdicts = directory / 'fixture.json', directory / 'verdicts.json'
        fixture.write_text(json.dumps(dict(cases=cases)), encoding='utf-8')
        process = subprocess.run([str(EXE), 'selftest-runtime', '--fixture', str(fixture),
                                  '--json', str(verdicts), '--workspace', str(directory / 'runs')],
                                 cwd=ROOT, env=dict(os.environ, DOTNET_ROOT=str(ROOT / '.runtime/dotnet')),
                                 capture_output=True, encoding='utf-8', errors='replace', timeout=90)
        assert verdicts.is_file(), process.stdout + process.stderr
        rows = json.loads(verdicts.read_text(encoding='utf-8'))['cases']
        assert len(rows) == len(cases)
        for (name, response, success), row in zip(variants, rows):
            assert row['name'] == name and row['ok'], (name, row)
            task = row['tasks'][0]
            artifact = json.loads(Path(task['artifact']).read_text(encoding='utf-8'))
            state = json.loads((Path(row['run_directory']) / 'state.json').read_text(encoding='utf-8'))
            assert ('snapshot' in state['completed']) is success, name
            if success:
                assert artifact['evidence']['semantics'] == 'stored_config', name
            else:
                assert task['error_kind'] == 'contract_violation', (name, task)
                assert artifact['evidence']['host_response'] == response, name
        assert process.returncode in (0, 1), process.stdout + process.stderr
    print(f'  OK {len(cases)} Core 调度快照响应/断点场景（无宿主或设备）')


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先 dotnet build）')
        return 1
    verify_host_validation()
    verify_response_contract()
    if not ARGS_JSON.is_file() or not CONFIG.is_file():
        print('[跳过] 缺少上游 args.json 或账号配置 config/alas.json；本验收未跑。')
        return 0

    failures: list[str] = []
    tasks = sorted(json.loads(ARGS_JSON.read_text(encoding='utf-8')).keys())
    config = json.loads(CONFIG.read_text(encoding='utf-8'))
    expected_enabled = sorted(t for t in tasks
                              if isinstance(config.get(t), dict)
                              and isinstance(config[t].get('Scheduler'), dict)
                              and config[t]['Scheduler'].get('Enable') is True)
    expected_no_scheduler = sorted(t for t in tasks
                                   if not (isinstance(config.get(t), dict)
                                           and isinstance(config[t].get('Scheduler'), dict)))

    before = sha256(CONFIG)
    real = run_task('alas', only_enabled=True)
    after = sha256(CONFIG)

    print('=== 与独立读数对拍（真配置）===')
    checks = [
        ('任务结论 succeeded', real.get('_outcome') == 'succeeded',
         f"outcome={real.get('_outcome')} error={real.get('_error')}"),
        ('任务总数一致', real.get('task_count') == len(tasks),
         f"任务={real.get('task_count')} 独立={len(tasks)}"),
        ('启用数一致', real.get('enabled_count') == len(expected_enabled),
         f"任务={real.get('enabled_count')} 独立={len(expected_enabled)}"),
        ('无 Scheduler 数一致', real.get('no_scheduler_count') == len(expected_no_scheduler),
         f"任务={real.get('no_scheduler_count')} 独立={len(expected_no_scheduler)}"),
        ('列出的都是启用项', all(e.get('enable') is True for e in real.get('listed') or []),
         f"listed={real.get('listed')[:3]}"),
        ('只读：配置字节不变', before == after, f'{before[:12]} → {after[:12]}'),
        ('明确标注存储快照', real.get('semantics') == 'stored_config', '不能表示为原生有效调度'),
        ('默认实例身份', real.get('instance') == 'alas', f"instance={real.get('instance')}"),
        ('CLI 打出 [任务证据] 行（键名改掉会静默消失）', '[任务证据]' in real.get('_stdout', '') and '启用=' in real.get('_stdout', ''), 'stdout 里没有 [任务证据] 或 启用='),
    ]
    for name, ok, detail in checks:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    invalid_inputs = {
        'unknown-field': {'only_enabled': True, 'limit': 200,
                          'instance': 'alas', 'extra': True},
        'only-enabled-type': {'only_enabled': 'true', 'limit': 200,
                              'instance': 'alas'},
        'limit-zero': {'only_enabled': True, 'limit': 0,
                       'instance': 'alas'},
        'limit-fraction': {'only_enabled': True, 'limit': 1.5,
                           'instance': 'alas'},
        'instance-type': {'only_enabled': True, 'limit': 200, 'instance': 7},
        'instance-path': {'only_enabled': True, 'limit': 200, 'instance': '../alas'},
        'instance-noncanonical': {'only_enabled': True, 'limit': 200, 'instance': 'alas.'},
    }
    for name, input_value in invalid_inputs.items():
        invalid = run_task('alas', input_value=input_value)
        ok = (invalid.get('_outcome') == 'skipped'
              and invalid.get('_error_kind') == 'none'
              and bool(invalid.get('_unmet_preconditions'))
              and not invalid.get('task_count'))
        print(f"  {'ok  ' if ok else 'FAIL'} {name}: outcome={invalid.get('_outcome')} "
              f"preconditions={invalid.get('_unmet_preconditions')}")
        if not ok:
            failures.append(f'{name}: invalid input reached task_schedule: {invalid}')

    # ---- 边界：用构造的假配置走同一条代码路径
    print()
    print('=== 边界情形（构造的假配置）===')
    nonce = uuid.uuid4().hex[:12]
    created = []
    try:
        fixtures = {
            'all-disabled': {t: {'Scheduler': {'Enable': False}} for t in tasks},
            'all-enabled': {t: {'Scheduler': {'Enable': True, 'NextRun': '2026-01-01 00:00:00'}}
                            for t in tasks},
            'no-scheduler': {t: {'Other': {}} for t in tasks},
            'stored-values': {'Main': {'Scheduler': {'Enable': False}},
                              'Commission': {'Scheduler': {'Enable': False}},
                              'Tactical': {'Scheduler': {}}},
        }
        for name, payload in fixtures.items():
            instance = f'codex_sched_{nonce}_{name.replace("-", "_")}'
            path = CONFIG.parent / f'{instance}.json'
            with path.open('x', encoding='utf-8') as stream:
                json.dump(payload, stream)
            created.append(path)
            fixture_hash = sha256(path)
            evidence = run_task(instance, only_enabled=name != 'stored-values')
            if name == 'all-disabled':
                # 断言用**任务证据里真实存在的字段**（listed），不要用宿主 op 的 listed_count ——
                # 任务类没有把它抄进证据，那是它自己的取舍；测试不该假设它存在。
                ok = evidence.get('_outcome') == 'succeeded' and evidence.get('enabled_count') == 0 \
                     and evidence.get('listed') == []
            elif name == 'all-enabled':
                ok = evidence.get('_outcome') == 'succeeded' \
                     and evidence.get('enabled_count') == len(tasks)
            elif name == 'no-scheduler':
                ok = evidence.get('_outcome') == 'succeeded' \
                     and evidence.get('no_scheduler_count') == len(tasks) \
                     and evidence.get('enabled_count') == 0
            else:
                entries = {entry['task']: entry for entry in evidence.get('listed', [])}
                ok = (evidence.get('_outcome') == 'succeeded'
                      and entries.get('Commission', {}).get('enable') is False
                      and entries.get('Main', {}).get('enable') is False
                      and entries.get('Tactical', {}).get('enable') is None
                      and entries.get('Tactical', {}).get('scheduler_present') is True
                      and evidence.get('enabled_count') == 0)
            ok = (ok and sha256(path) == fixture_hash
                  and evidence.get('semantics') == 'stored_config'
                  and evidence.get('instance') == instance)
            print(f"  {'ok  ' if ok else 'FAIL'} {name}: enabled={evidence.get('enabled_count')} "
                  f"no_scheduler={evidence.get('no_scheduler_count')} "
                  f"outcome={evidence.get('_outcome')}")
            if not ok:
                failures.append(f'{name}: {evidence}')

        # These values used to become True/False through Python truthiness.
        # Reject the entire snapshot even when an invalid row would be filtered.
        invalid_values = ('false', 'true', '', 0, 1, None, [], {}, [False])
        for index, value in enumerate(invalid_values):
            instance = f'codex_sched_{nonce}_invalid_enable_{index}'
            path = CONFIG.parent / f'{instance}.json'
            with path.open('x', encoding='utf-8') as stream:
                json.dump({'Main': {'Scheduler': {'Enable': value}}}, stream)
            created.append(path)
            before = sha256(path)
            evidence = run_task(instance, only_enabled=True)
            ok = (evidence.get('_outcome') == 'failed'
                  and 'Main.Scheduler.Enable' in str(evidence.get('_error'))
                  and not evidence.get('listed') and sha256(path) == before)
            print(f"  {'ok  ' if ok else 'FAIL'} Enable type {type(value).__name__}: {value!r}")
            if not ok:
                failures.append(f'非法 Enable {value!r}: {evidence}')

        missing = run_task(f'codex_sched_{nonce}_not_exists', only_enabled=True)
        ok = missing.get('_outcome') == 'failed' and '读不到' in str(missing.get('_error') or '')
        print(f"  {'ok  ' if ok else 'FAIL'} 配置不存在: outcome={missing.get('_outcome')} "
              f"error={str(missing.get('_error'))[:60]}")
        if not ok:
            failures.append(f"配置不存在时应 Failed 且给出原因，实为 {missing}")
    finally:
        for path in created:
            path.unlink(missing_ok=True)

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（存储快照、未知启用状态、严格布尔类型、只读保证通过）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
