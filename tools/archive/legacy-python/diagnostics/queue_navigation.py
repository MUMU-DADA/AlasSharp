"""Run one page-navigation task through the product queue entry point."""

import json
import subprocess
import tempfile
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
ARTIFACTS = REPO / 'runs' / 'diagnostic-navigation'


def run_navigation(server_exe, page, serial, *, adb=None, timeout=900,
                   capture_output=True):
    with tempfile.TemporaryDirectory(prefix='alas-navigate-') as temp:
        queue = Path(temp) / 'queue.json'
        queue.write_text(json.dumps({'tasks': [
            {'id': 'navigate', 'kind': 'navigate', 'required': True,
             'input': {'to': page, 'rounds': 1}},
        ]}), encoding='utf-8')
        command = [str(server_exe), 'queue', '--file', str(queue), '--run',
                   '--allow-actions', '--serial', serial,
                   '--artifacts', str(ARTIFACTS)]
        if adb:
            command.extend(['--adb', adb])
        return subprocess.run(
            command, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE if capture_output else subprocess.DEVNULL,
            stderr=subprocess.PIPE if capture_output else subprocess.DEVNULL,
            text=True, encoding='utf-8', errors='replace', timeout=timeout)
