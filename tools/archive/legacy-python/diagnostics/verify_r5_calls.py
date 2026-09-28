#!/usr/bin/env python3
"""Audit recursive static call encoding; native dynamic callability is not established here."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / "src/Alas.Server/bin/Release/net10.0/Alas.Server.exe"
DATA = Path(os.environ.get("ALAS_DATA") or ROOT / "data")
STRUCTURAL = {"branch", "return", "local_set", "state_set", "map_set", "raise", "log"}
CALLS = {"call", "conditional", "conditional_negated", "terminal", "assign", "super_delegate"}


def inventory(data: Path) -> dict:
    files = sorted((data / "campaign").rglob("*.json"))
    if not files:
        raise ValueError("缺少 campaign 导出，不能将空扫描当作全量通过")
    counts = dict(plans=len(files), hooks=0, steps=0, structural=0, condition_calls=0, calls=0)
    call_sources = set()
    incomplete = set()

    def expressions(value, source):
        if isinstance(value, dict):
            for key, child in value.items():
                if key == "call" and isinstance(child, dict):
                    counts['condition_calls'] += 1
                    counts['calls'] += 1
                    call_sources.add(source + '.call')
                else:
                    expressions(child, source + '.' + key)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                expressions(child, f'{source}[{index}]')

    def steps(sequence, source):
        if not isinstance(sequence, list):
            raise ValueError(f'{source} 不是步骤数组')
        for index, step in enumerate(sequence):
            item = f'{source}[{index}]'
            counts['steps'] += 1
            if step.get('kind') in STRUCTURAL:
                counts['structural'] += 1
            elif step.get('kind') in CALLS:
                counts['calls'] += 1
                call_sources.add(item)
            else:
                raise ValueError(f'{item} 未知步骤类型')
            for key in ('test', 'expr', 'value'):
                expressions(step.get(key), item + '.' + key)
            for key in ('body', 'orelse'):
                if key in step:
                    steps(step[key], item + '.' + key)

    for path in files:
        document = json.loads(path.read_text(encoding='utf-8'))
        for battle in document['campaign']['battles']:
            counts['hooks'] += 1
            source = path.relative_to(data / 'campaign').as_posix() + ':' + battle['method']
            if battle.get('plan_complete') is not True:
                incomplete.add(source)
            steps(battle['steps'], source + '.steps')
    if not counts['hooks']:
        raise ValueError('导出没有钩子，不能完成调用审计')
    return dict(counts=counts, call_sources=call_sources, incomplete=incomplete)


def validate(payload: dict, expected: dict, returncode: int) -> list[str]:
    problems = []
    if returncode != 0:
        problems.append(f'调用审计退出码 {returncode}')
    if payload.get('scope') != 'recursive_static_encoding/1':
        problems.append('缺少递归静态编码审计合同')
    for key, count in expected['counts'].items():
        if payload.get(key) != count:
            problems.append(f'{key}: C#={payload.get(key)}，独立 JSON 遍历={count}')
    samples = payload.get('sample') or []
    failures = payload.get('unsupported') or []
    sources = [row['source'] for row in samples + failures]
    if set(sources) != expected['call_sources'] or len(sources) != len(set(sources)):
        problems.append('逐调用来源不完整、重复或包含非调用，不能据汇总数声称全量')
    actual_incomplete = {row['source'] for row in payload.get('incomplete_hooks') or []}
    if actual_incomplete != expected['incomplete']:
        problems.append('不完整钩子清单与导出不一致')
    if actual_incomplete:
        problems.append(f'{len(actual_incomplete)} 个钩子没有完整计划，调用面尚未覆盖')
    if failures:
        problems.append(f'{len(failures)} 个调用无法编码')
    unbound = payload.get('unresolved_bindings') or []
    if unbound:
        problems.append(f'{len(unbound)} 个方法绑定需要原生环境解析，不能证明可运行')
    if payload.get('dynamic_callability_verified') is not False:
        problems.append('静态检查不能宣称验证过动态可调用性')
    if any(row.get('dynamic_callable') is not None for row in samples):
        problems.append('调用样例不应冒充动态执行证据')
    static = sum(row.get('encoding') == 'static' for row in samples)
    runtime = sum(row.get('encoding') == 'runtime' for row in samples)
    if payload.get('static_encoded') != static or payload.get('runtime_resolved') != runtime:
        problems.append('静态编码与运行期求值计数不一致')
    if static + runtime + len(failures) != expected['counts']['calls']:
        problems.append('调用分类没有覆盖全部递归调用')
    return problems


def main() -> int:
    expected = inventory(DATA)
    result = subprocess.run([str(SERVER), 'r5-calls', '--data', str(DATA), '--json'],
                            cwd=ROOT, capture_output=True, text=True, timeout=600,
                            encoding='utf-8', errors='replace')
    if not result.stdout.lstrip().startswith('{'):
        print(result.stderr or result.stdout)
        return 1
    payload = json.loads(result.stdout)
    problems = validate(payload, expected, result.returncode)
    print(f"[r5-calls] 递归步骤 {payload['steps']} / 条件调用 {payload['condition_calls']} / "
          f"静态编码 {payload['static_encoded']} / 需环境求值 {payload['runtime_resolved']}；动态可调用性未验证")
    for problem in problems:
        print('FAIL: ' + problem)
    if not problems:
        print('PASS: 递归静态编码来源与导出一致；不证明动态方法可调用或实战成功')
    return int(bool(problems))


if __name__ == '__main__':
    sys.exit(main())
