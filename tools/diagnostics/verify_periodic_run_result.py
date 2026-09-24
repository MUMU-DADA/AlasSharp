"""Reject contradictory native dispatch evidence through the real Core queue.

The transport is inert; queue outcomes, artifacts and resume state are real.
No host, account configuration or device is accessed.
"""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / 'src/Alas.DataTool/bin/Release/net10.0/alashub.exe'


def cases_for(kind):
    task, method = ('Reward', 'reward') if kind == 'periodic_run' else ('OpsiExplore', 'opsi_explore')
    response = dict(task=task, instance='alas', decision='ran', allow_actions=True,
                    confirm_matches=True, constructed=True, ran=True, native_success=True,
                    target=dict(module='alas', **{'class': 'AzurLaneAutoScript'},
                                scheduler_command=task, method=method), elapsed_s=0.1)
    variants = [('success', response, True)]
    for field in ('constructed', 'ran', 'native_success', 'allow_actions', 'confirm_matches'):
        for value in (False, None):
            variants.append((f'{field}_{value}', dict(response, **{field: value}), False))
        missing = copy.deepcopy(response)
        del missing[field]
        variants.append((f'{field}_missing', missing, False))
    for field in ('task', 'instance'):
        variants.append((f'{field}_wrong', dict(response, **{field: 'another'}), False))
        variants.append((f'{field}_missing', {k: v for k, v in response.items() if k != field}, False))
    for field in ('module', 'class', 'method', 'scheduler_command'):
        changed = copy.deepcopy(response)
        changed['target'][field] = ''
        variants.append((f'target_{field}_empty', changed, False))
    variants += [('target_missing', {k: v for k, v in response.items() if k != 'target'}, False),
                 ('error_with_success', dict(response, error='fixture native failure',
                                             traceback_tail=['fixture.py:1 run'],
                                             failure_frames=['fixture-frame.png']), False)]
    for label, reported, success in variants:
        expected = 'succeeded' if success else 'failed'
        case = dict(name=f'{kind}_{label}', mode='queue', dry_run=False, allow_actions=True,
                    artifacts=True, tasks=[dict(id='run', kind=kind, required=True,
                    input=dict(task=task, allow_actions=True, confirm=task))],
                    stub_responses={'periodic_run': [{'result': reported}]},
                    expect=dict(outcome=expected, cleared=False,
                                tasks=[dict(id='run', outcome=expected)]))
        if kind == 'os_action':
            case['stub_responses']['periodic_plan'] = [dict(result=dict(
                task=task, found=True, scheduler_command=task, method=method, lineno=1))]
        yield case


def main():
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    output_root = ROOT / '.runtime/verification'
    output_root.mkdir(parents=True, exist_ok=True)
    cases = list(cases_for('periodic_run')) + list(cases_for('os_action'))
    failures = []
    with tempfile.TemporaryDirectory(prefix='periodic-result-', dir=output_root) as folder:
        directory = Path(folder)
        fixture, verdicts = directory / 'fixture.json', directory / 'verdicts.json'
        fixture.write_text(json.dumps(dict(cases=cases)), encoding='utf-8')
        process = subprocess.run([str(EXE), 'selftest-runtime', '--fixture', str(fixture),
                                  '--json', str(verdicts), '--workspace', str(directory / 'runs')],
                                 cwd=ROOT, env=dict(os.environ, DOTNET_ROOT=str(ROOT / '.runtime/dotnet')),
                                 capture_output=True, encoding='utf-8', errors='replace', timeout=90)
        if not verdicts.is_file():
            raise AssertionError(process.stdout + process.stderr)
        actual = json.loads(verdicts.read_text(encoding='utf-8'))['cases']
        assert len(actual) == len(cases)
        for expected, row in zip(cases, actual):
            name = expected['name']
            if row['name'] != name or not row['ok']:
                failures.append((name, 'unexpected outcome'))
                continue
            artifact = json.loads(Path(row['tasks'][0]['artifact']).read_text(encoding='utf-8'))
            evidence = artifact['evidence']
            reported = expected['stub_responses']['periodic_run'][0]['result']
            # Store the actual host echo, never replace it with the requested
            # identity or authorization when they contradict one another.
            problems = []
            for field in ('task', 'instance', 'allow_actions', 'confirm_matches',
                          'constructed', 'ran', 'native_success', 'error'):
                if evidence.get(field) != reported.get(field):
                    problems.append(f'changed host evidence: {field}')
            if name.endswith('error_with_success'):
                assert evidence['traceback_tail'] == reported['traceback_tail']
                assert evidence['failure_frames'] == reported['failure_frames']
            successful = expected['expect']['outcome'] == 'succeeded'
            state = json.loads((Path(row['run_directory']) / 'state.json').read_text(encoding='utf-8'))
            assert ('run' in state['completed']) is successful, (name, state['completed'])
            if not successful:
                if row['tasks'][0]['error_kind'] != 'contract_violation':
                    problems.append('inconsistent response not classified as contract violation')
                assert evidence['response_violations'], name
            if problems:
                failures.append((name, '; '.join(problems)))
            else:
                print(f'PASS: {name}')
        for name, problem in failures:
            print(f'FAIL: {name}: {problem}')
        assert process.returncode in (0, 1), process.stdout + process.stderr
    print(f'{len(cases) - len(failures)}/{len(cases)} Core periodic/OS response checks; no device actions')
    return int(bool(failures))


if __name__ == '__main__':
    raise SystemExit(main())
