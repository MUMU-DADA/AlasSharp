#!/usr/bin/env python3
"""Check actual primitive invocations and expose every missing coverage prerequisite."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys

from verify_r5_execution import EXPECTED_PRIMITIVES

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'src/Alas.Server/bin/Release/net10.0/Alas.Server.exe'
DATA = Path(os.environ.get('ALAS_DATA') or ROOT / 'data')
EXEC_FIXTURE = ROOT / 'tools/diagnostics/r5-execution-fixture.json'
LOOP_FIXTURE = ROOT / 'tools/diagnostics/r5-loop-fixture.json'


def normalize(name: str) -> str:
    for prefix in ('fleet_1.', 'fleet_2.', 'fleet_boss.', 'fleet_submarine.'):
        if name.startswith(prefix):
            return name[len(prefix):]
    return name


def run(command: str, fixture: Path) -> dict:
    result = subprocess.run([str(SERVER), command, '--fixture', str(fixture), '--data', str(DATA), '--json'],
                            cwd=ROOT, capture_output=True, text=True, timeout=600,
                            encoding='utf-8', errors='replace')
    if result.returncode != 0:
        raise RuntimeError(f'{command} 退出码 {result.returncode}: {result.stderr[-500:]}')
    return json.loads(result.stdout)


def observed(payload: dict, fixture: Path) -> set[str]:
    fixtures = json.loads(fixture.read_text(encoding='utf-8')).get('cases')
    cases = payload.get('cases')
    if not isinstance(fixtures, list) or not fixtures or not isinstance(cases, list) or not cases:
        raise ValueError('缺少非空夹具/执行结果，不能统计覆盖率')
    expected = [case['name'] for case in fixtures]
    actual = [case.get('name') for case in cases]
    if len(actual) != len(set(actual)) or sorted(actual) != sorted(expected):
        raise ValueError('实际结果没有逐项覆盖夹具用例')
    covered = set()
    for case in cases:
        invocations = case.get('invoked_ops')
        if not isinstance(invocations, list) or any(not isinstance(op, str) or not op for op in invocations):
            raise ValueError(f"{case.get('name')} 缺少真实 invoked_ops；动作、步骤文本和日志不能代替调用记录")
        covered.update(normalize(op) for op in invocations)
    return covered


def plan_gaps(data: Path, registered: set[str]) -> tuple[list[str], list[str]]:
    paths = sorted((data / 'campaign').rglob('*.json'))
    if not paths:
        raise ValueError('缺少 campaign 导出，不能统计计划覆盖')
    incomplete, unresolved = [], []
    hook_count = 0

    def calls(value):
        if isinstance(value, dict):
            # Both step objects and nested condition-call objects carry op.
            if value.get('op') and ('kind' in value or 'args' in value):
                yield normalize(value['op'])
            for key, child in value.items():
                if key == 'call' and isinstance(child, dict) and child.get('op'):
                    yield normalize(child['op'])
                else:
                    yield from calls(child)
        elif isinstance(value, list):
            for child in value:
                yield from calls(child)

    for path in paths:
        battles = json.loads(path.read_text(encoding='utf-8'))['campaign']['battles']
        hooks = {battle['method'] for battle in battles}
        for battle in battles:
            hook_count += 1
            source = path.relative_to(data / 'campaign').as_posix() + ':' + battle['method']
            if battle.get('plan_complete') is not True:
                incomplete.append(source)
                continue
            for op in set(calls(battle['steps'])) - registered - hooks - {'map.select'}:
                unresolved.append(source + ':' + op)
    if hook_count == 0:
        raise ValueError('导出没有钩子，不能统计计划覆盖')
    return incomplete, unresolved


def main() -> int:
    registered = set(EXPECTED_PRIMITIVES)
    incomplete, unresolved = plan_gaps(DATA, registered)
    execution = run('r5-exec', EXEC_FIXTURE)
    if set(execution.get('implemented_primitives') or []) != registered:
        raise ValueError('实际注册表与执行回归的原语词表不一致')
    covered = observed(execution, EXEC_FIXTURE) | observed(run('r5-loop', LOOP_FIXTURE), LOOP_FIXTURE)
    missing = sorted(registered - covered)
    print(f'[r5-coverage] 注册原语 {len(registered)}；真实调用覆盖 {len(registered & covered)}；缺口 {len(missing)}')
    for op in missing:
        print('  未调用: ' + op)
    print(f'计划缺口：不完整钩子 {len(incomplete)}；需确认方法绑定 {len(unresolved)}')
    for source in (incomplete + unresolved)[:20]:
        print('  ' + source)
    if missing or incomplete or unresolved:
        print('FAIL: 覆盖不完整；登记原因、日志命中或静态步骤均不能抵消缺口')
        return 1
    print('PASS: 登记原语均有真实调用记录且扫描计划无缺口；不证明真机行为')
    return 0


if __name__ == '__main__':
    sys.exit(main())
