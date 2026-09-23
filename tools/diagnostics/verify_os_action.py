#!/usr/bin/env python3
"""Check the production OS action queue entry without authorizing device actions."""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory


ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'


def main() -> int:
    if not EXE.is_file():
        print('FAIL: build Alas.DataTool in Release before verifying os_action')
        return 1
    with TemporaryDirectory(prefix='alas-os-action-') as temporary:
        root = Path(temporary)
        queue_file = root / 'queue-input.json'
        queue_file.write_text(json.dumps({'tasks': [{
            'id': 'os-probe', 'kind': 'os_action',
            'input': {'task': 'OpsiExplore', 'allow_actions': True,
                      'confirm': 'OpsiExplore'},
        }]}), encoding='utf-8')
        artifacts = root / 'artifacts'
        process = subprocess.run(
            [str(EXE), 'queue', '--file', str(queue_file), '--artifacts', str(artifacts)],
            cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace',
            timeout=120)
        task_files = list(artifacts.glob('*/task-os-probe.json'))
        queue_files = list(artifacts.glob('*/queue.json'))
        if process.returncode != 0 or len(task_files) != 1 or len(queue_files) != 1:
            print(f'FAIL: CLI return={process.returncode}, task files={len(task_files)}, '
                  f'queue files={len(queue_files)}')
            return 1
        task = json.loads(task_files[0].read_text(encoding='utf-8'))
        queue = json.loads(queue_files[0].read_text(encoding='utf-8'))
        checks = {
            'os_action registered in production queue': task['kind'] == 'os_action',
            'dry-run action skipped with reason': task['outcome'] == 'skipped'
                and '会话未授权' in str(task.get('error')),
            'no action evidence fabricated': task.get('evidence') is None,
            'queue records no device configuration': queue['dry_run'] is True
                and queue['device_configure_count'] == 0,
            'one persistent host and one task artifact': queue['host_start_count'] == 1
                and len(queue['tasks']) == 1
                and Path(str(queue['tasks'][0]['artifact'])).name == task_files[0].name,
        }
        non_os_file = root / 'non-os-input.json'
        non_os_file.write_text(json.dumps({'tasks': [{
            'id': 'non-os', 'kind': 'os_action',
            'input': {'task': 'reward', 'allow_actions': True, 'confirm': 'reward'},
        }]}), encoding='utf-8')
        non_os_artifacts = root / 'non-os-artifacts'
        rejected = subprocess.run(
            [str(EXE), 'queue', '--file', str(non_os_file), '--artifacts',
             str(non_os_artifacts), '--run', '--allow-actions'],
            cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace',
            timeout=120)
        rejected_tasks = list(non_os_artifacts.glob('*/task-non-os.json'))
        rejected_queues = list(non_os_artifacts.glob('*/queue.json'))
        if len(rejected_tasks) != 1 or len(rejected_queues) != 1:
            print('FAIL: authorized non-OS rejection did not leave task and queue artifacts')
            return 1
        rejected_task = json.loads(rejected_tasks[0].read_text(encoding='utf-8'))
        rejected_queue = json.loads(rejected_queues[0].read_text(encoding='utf-8'))
        rejected_evidence = rejected_task.get('evidence') or {}
        checks['real upstream non-OS target rejected before device actions'] = (
            rejected.returncode == 1 and rejected_task['outcome'] == 'failed'
            and rejected_evidence.get('os_plan', {}).get('method') == 'reward'
            and 'target' not in rejected_evidence and 'ran' not in rejected_evidence
            and rejected_queue['device_configure_count'] == 0)
        for label, passed in checks.items():
            print(f'{"PASS" if passed else "FAIL"}: {label}')
        return 0 if all(checks.values()) else 1


if __name__ == '__main__':
    sys.exit(main())
