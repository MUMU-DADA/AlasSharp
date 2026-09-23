# -*- coding: utf-8 -*-
"""Verify relative runtime paths keep the caller's working directory as their base."""
from __future__ import annotations

import json
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory


ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'
CHAPTER = 'campaign.campaign_main.campaign_2_1'


def run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([str(EXE), *args], cwd=ROOT, capture_output=True,
                          text=True, encoding='utf-8', errors='replace', timeout=180)


def main() -> int:
    if not EXE.is_file():
        print('missing Release alashub.exe; build the project first')
        return 1
    runtime = ROOT / '.runtime'
    runtime.mkdir(exist_ok=True)
    with TemporaryDirectory(prefix='artifact-paths-', dir=runtime) as temporary:
        workspace = Path(temporary)
        queue_file = workspace / 'queue-input.json'
        queue_file.write_text(json.dumps({'tasks': [
            {'id': 'rules', 'kind': 'campaign_batch', 'input': {'chapters': [CHAPTER]}},
        ]}), encoding='utf-8')
        artifacts = workspace / 'artifacts'
        relative_artifacts = str(artifacts.relative_to(ROOT))
        first = run('queue', '--file', str(queue_file), '--artifacts', relative_artifacts)
        directories = list(artifacts.iterdir()) if artifacts.exists() else []
        if first.returncode != 0 or len(directories) != 1:
            print(f'first run failed: exit={first.returncode}, dirs={directories}')
            print(first.stdout[-1000:])
            return 1
        first_run = directories[0]
        state = first_run / 'state.json'
        if not all((first_run / name).is_file()
                   for name in ('queue.json', 'index.json', 'session-log.jsonl', 'state.json')):
            print('first run did not write all artifacts under the caller-relative root')
            return 1

        second = run('queue', '--file', str(queue_file), '--artifacts', relative_artifacts,
                     '--resume', '--resume-state', str(state.relative_to(ROOT)))
        runs = sorted(artifacts.iterdir())
        if second.returncode != 0 or len(runs) != 2:
            print(f'resume failed: exit={second.returncode}, dirs={runs}')
            print(second.stdout[-1000:])
            return 1
        resumed = json.loads((runs[-1] / 'queue.json').read_text(encoding='utf-8'))
        if resumed['tasks'][0]['outcome'] != 'skipped':
            print(f'resume did not skip the completed task: {resumed["tasks"]}')
            return 1

        stop = workspace / 'stop.request'
        stop.touch()
        stopped = run('queue', '--file', str(queue_file), '--artifacts', relative_artifacts,
                      '--stop-file', str(stop.relative_to(ROOT)))
        runs = sorted(artifacts.iterdir())
        if stopped.returncode != 0 or len(runs) != 3:
            print(f'stop-file run failed: exit={stopped.returncode}, dirs={runs}')
            print(stopped.stdout[-1000:])
            return 1
        cancelled = json.loads((runs[-1] / 'queue.json').read_text(encoding='utf-8'))
        if cancelled['outcome'] != 'cancelled' or cancelled['tasks'][0]['outcome'] != 'skipped':
            print(f'relative stop-file was not observed: {cancelled}')
            return 1
    print('OK: relative artifacts, resume state and stop file keep the caller path base')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
