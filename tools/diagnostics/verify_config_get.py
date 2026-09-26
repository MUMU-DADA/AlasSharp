# -*- coding: utf-8 -*-
"""配置开关域验收：`kind = "config_get"`（只读，读账号配置里的开关值）。

三段：

1. **与独立读数对拍**：任务报的 `true_keys` / `false_keys` / `missing_keys`，与脚本**自己再读一遍**
   `config/alas.json` 的结果逐项一致 —— 单侧读错不会两边一起错。
2. **核心语义**：**缺失 ≠ false**。构造一个必然缺失的键，断言它出现在 `missing_keys` 里、
   **并且不在** `false_keys` 里 —— 这条是该域存在的理由（授权前判"会不会花钱"时，
   把"没设置"误当成"关掉了"就是错的）。
3. **边界**：空 `keys` → 前置条件不满足记 `skipped`（不是 failed、也不该去读配置）；不存在的深层路径 → missing。
4. **实例隔离**：两份临时合成配置同键异值，产品队列必须按 `instance` 读取，结束后删除合成文件。

用法：
    python tools/diagnostics/verify_config_get.py
"""

from __future__ import annotations

import json
import copy
import os
import subprocess
import sys

import dotnet_env  # noqa: E402  （同目录的 .NET 环境解析）
import tempfile
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / '.runtime' / 'engine' / 'config' / 'alas.json'
EXE = ROOT / 'src' / 'Alas.Server' / 'bin' / 'Release' / 'net10.0' / 'Alas.Server.exe'
LAST_STDOUT = ''          # 上一任务的 CLI 输出：给'打印机没静默失效'这条断言用

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def run_task(keys, timeout=300, *, input_value=None, instance=None):
    """跑一次队列任务，返回 (outcome, evidence, error)。"""
    with tempfile.TemporaryDirectory(prefix='alas-config-') as tmp:
        queue_file = Path(tmp) / 'queue.json'
        task_input = {'keys': keys} if input_value is None else input_value
        if instance is not None:
            task_input['instance'] = instance
        queue_file.write_text(json.dumps({'tasks': [
            {'id': 'cfg', 'kind': 'config_get',
             'input': task_input}]},
            ensure_ascii=False), encoding='utf-8')
        artifacts = Path(tmp) / 'artifacts'
        proc = subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                        '--artifacts', str(artifacts)],
                       capture_output=True, text=True, encoding='utf-8',
                       errors='replace', timeout=timeout)
        global LAST_STDOUT
        LAST_STDOUT = proc.stdout or ''
        artifact = next(iter(sorted(artifacts.glob('*/task-*.json'))), None)
        if artifact is None:
            return None, {}, '没有工件'
        document = json.loads(artifact.read_text(encoding='utf-8'))
        return document.get('outcome'), (document.get('evidence') or {}), document.get('error')


def walk(config, dotted):
    node = config
    for part in dotted.split('.'):
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def verify_instances(failures):
    print('=== 双实例离线隔离 ===')
    nonce = uuid.uuid4().hex[:12]
    first = f'codex_config_probe_a_{nonce}'
    second = f'codex_config_probe_b_{nonce}'
    files = [CONFIG.parent / f'{name}.json' for name in (first, second)]
    try:
        for file, flag in zip(files, (True, False)):
            with file.open('x', encoding='utf-8') as stream:
                json.dump({'Probe': {'Flag': flag, 'Count': 3}}, stream)
        probe_keys = ['Probe.Flag', 'Probe.Count', 'Probe.Absent']
        for name, flag in ((first, True), (second, False)):
            selected, observed, selected_error = run_task(probe_keys, instance=name)
            ok = (selected == 'succeeded' and observed.get('instance') == name
                  and Path(observed.get('config_source') or '').name == f'{name}.json'
                  and observed.get('values') == {'Probe.Flag': flag,
                                                  'Probe.Count': 3,
                                                  'Probe.Absent': None}
                  and observed.get('true_keys') == (['Probe.Flag'] if flag else [])
                  and observed.get('false_keys') == ([] if flag else ['Probe.Flag'])
                  and observed.get('missing_keys') == ['Probe.Absent'])
            print(f"  {'ok  ' if ok else 'FAIL'} {name} 独立读数")
            if not ok:
                failures.append(f'{name} 独立读数: outcome={selected} '
                                f'evidence={observed} error={selected_error}')
        absent, _, absent_error = run_task(probe_keys, instance=f'codex_config_probe_missing_{nonce}')
        ok = absent == 'failed' and bool(absent_error)
        print(f"  {'ok  ' if ok else 'FAIL'} 不存在实例记 failed")
        if not ok:
            failures.append(f'不存在实例: outcome={absent} error={absent_error}')
    finally:
        for file in files:
            file.unlink(missing_ok=True)


def verify_response_contract():
    """Malformed host snapshots must not become successful configuration evidence."""
    base = dict(instance='alas', config_source='synthetic.json', checked=2,
                values={'Probe.Flag': True, 'Probe.Absent': None},
                missing=['Probe.Absent'])
    variants = [('valid', base, True)]
    for name, change in (
        ('wrong-instance', {'instance': 'other'}),
        ('empty-source', {'config_source': ''}),
        ('wrong-count', {'checked': 1}),
        ('extra-key', {'values': {**base['values'], 'Other.Flag': False}}),
        ('missing-key', {'values': {'Probe.Flag': True}}),
        ('missing-list', {'missing': []}),
        ('duplicate-missing', {'missing': ['Probe.Absent', 'Probe.Absent']}),
        ('wrong-missing', {'missing': ['Probe.Flag']}),
    ):
        variants.append((name, {**copy.deepcopy(base), **change}, False))
    for name, field in (('absent-values', 'values'), ('absent-missing', 'missing'),
                        ('absent-checked', 'checked')):
        changed = copy.deepcopy(base)
        del changed[field]
        variants.append((name, changed, False))
    variants.append(('wrong-values-type', {**base, 'values': []}, False))
    cases = [dict(name=name, mode='queue', dry_run=True, artifacts=True,
                  tasks=[dict(id='cfg', kind='config_get', required=True,
                              input=dict(keys=['Probe.Flag', 'Probe.Absent']))],
                  stub_responses={'config_get': [dict(result=response)]},
                  expect=dict(outcome='succeeded' if valid else 'failed', cleared=False,
                              tasks=[dict(id='cfg', outcome='succeeded' if valid else 'failed')]))
             for name, response, valid in variants]
    with tempfile.TemporaryDirectory(prefix='alas-config-contract-') as temporary:
        directory = Path(temporary)
        fixture, verdicts = directory / 'fixture.json', directory / 'verdicts.json'
        fixture.write_text(json.dumps(dict(cases=cases)), encoding='utf-8')
        process = subprocess.run([str(EXE), 'selftest-runtime', '--fixture', str(fixture),
                                  '--json', str(verdicts), '--workspace', str(directory / 'runs')],
                                 cwd=ROOT, env=dotnet_env.apply(os.environ, ROOT),
                                 capture_output=True, text=True, encoding='utf-8',
                                 errors='replace', timeout=90)
        assert verdicts.is_file(), process.stdout + process.stderr
        rows = json.loads(verdicts.read_text(encoding='utf-8'))['cases']
        assert len(rows) == len(cases)
        for (name, response, valid), row in zip(variants, rows):
            assert row['name'] == name and row['ok'], (name, row)
            task = row['tasks'][0]
            artifact = json.loads(Path(task['artifact']).read_text(encoding='utf-8'))
            if valid:
                assert artifact['evidence']['values'] == response['values'], name
            else:
                assert task['error_kind'] == 'contract_violation', (name, task)
                assert artifact['evidence']['host_response'] == response, name
        assert process.returncode in (0, 1), process.stdout + process.stderr
    print(f'  OK {len(cases)} Core 配置读取响应场景（无宿主或设备）')


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先 dotnet build）')
        return 1
    verify_response_contract()
    if not CONFIG.is_file():
        print('[跳过] 没有账号配置 config/alas.json；仅运行合成双实例验收。')
        failures: list[str] = []
        verify_instances(failures)
        return 1 if failures else 0

    failures: list[str] = []
    config = json.loads(CONFIG.read_text(encoding='utf-8'))

    # 从真配置里挑三个键：一个 true、一个 false、一个必然缺失
    flat = {}

    def flatten(node, prefix=''):
        if isinstance(node, dict):
            for key, value in node.items():
                flatten(value, f'{prefix}.{key}' if prefix else key)
        else:
            flat[prefix] = node

    flatten(config)
    true_key = next((k for k, v in flat.items() if v is True), None)
    false_key = next((k for k, v in flat.items() if v is False), None)
    missing_key = 'No.Such.Section.No.Such.Key'
    keys = [k for k in (true_key, false_key, missing_key) if k]

    outcome, evidence, error = run_task(keys)
    print('=== 与独立读数对拍（真配置）===')
    expected_true = {k for k in keys if walk(config, k) is True}
    expected_false = {k for k in keys if walk(config, k) is False}
    expected_missing = {k for k in keys if walk(config, k) is None}
    checks = [
        ('任务结论 succeeded', outcome == 'succeeded', f'outcome={outcome} error={error}'),
        ('true_keys 与独立读一致', set(evidence.get('true_keys') or []) == expected_true,
         f"任务={evidence.get('true_keys')} 独立={sorted(expected_true)}"),
        ('false_keys 与独立读一致', set(evidence.get('false_keys') or []) == expected_false,
         f"任务={evidence.get('false_keys')} 独立={sorted(expected_false)}"),
        ('missing_keys 与独立读一致', set(evidence.get('missing_keys') or []) == expected_missing,
         f"任务={evidence.get('missing_keys')} 独立={sorted(expected_missing)}"),
        # 打印机会静默失效（键名一改这行就没了），所以连它一起断言
        ('CLI 打出 [任务证据] 行', '[任务证据]' in LAST_STDOUT and '查了=' in LAST_STDOUT,
         'stdout 里没有 [任务证据] 或 查了='),
        ('checked 等于请求的键数', evidence.get('checked') == len(keys),
         f"checked={evidence.get('checked')} 请求={len(keys)}"),
        # 核心语义：缺失**不能**被算成 false
        ('核心语义：缺失 ≠ false',
         missing_key in (evidence.get('missing_keys') or [])
         and missing_key not in (evidence.get('false_keys') or []),
         f"missing={evidence.get('missing_keys')} false={evidence.get('false_keys')}"),
    ]
    for name, ok, detail in checks:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    print()
    print('=== 边界 ===')
    empty_outcome, _, _ = run_task([])
    deep_outcome, deep_evidence, _ = run_task(['Dorm.NoSuchField.Enable'])
    boundary = [
        ('空 keys → skipped（不是 failed，也不该去读配置）',
         empty_outcome == 'skipped', f'outcome={empty_outcome}'),
        ('不存在的深层路径 → missing',
         deep_outcome == 'succeeded'
         and 'Dorm.NoSuchField.Enable' in (deep_evidence.get('missing_keys') or []),
         f'outcome={deep_outcome} missing={deep_evidence.get("missing_keys")}'),
    ]
    for name, ok, detail in boundary:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    invalid_inputs = {
        'missing-keys': {},
        'wrong-array-type': {'keys': 'Dorm.BuyFurniture.Enable'},
        'null-entry': {'keys': [None]},
        'number-entry': {'keys': [7]},
        'empty-entry': {'keys': [' ']},
        'mixed-entry': {'keys': ['Dorm.BuyFurniture.Enable', False]},
        'unknown-field': {'keys': ['Dorm.BuyFurniture.Enable'], 'extra': True},
        'invalid-instance-type': {'keys': ['Probe.Flag'], 'instance': 7},
        'empty-instance': {'keys': ['Probe.Flag'], 'instance': ''},
        'noncanonical-instance': {'keys': ['Probe.Flag'], 'instance': 'alas.'},
        'path-instance': {'keys': ['Probe.Flag'], 'instance': '../alas'},
    }
    for name, task_input in invalid_inputs.items():
        invalid_outcome, invalid_evidence, invalid_error = run_task(
            [], input_value=task_input)
        ok = (invalid_outcome == 'skipped' and not invalid_evidence
              and '前置条件不满足' in (invalid_error or ''))
        detail = f'outcome={invalid_outcome} error={invalid_error}'
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    print()
    verify_instances(failures)

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print(f'结果: OK（与独立读数一致；缺失 ≠ false；空输入记 skipped）  抽查键={keys}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
