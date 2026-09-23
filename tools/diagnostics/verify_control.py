# -*- coding: utf-8 -*-
"""Exercise the loopback control UI against the real queue runtime without a device."""

from __future__ import annotations

import json
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'
CHAPTER = 'campaign.campaign_main.campaign_1_1'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except AttributeError:
    pass


def request(base: str, path: str, *, data: dict | None = None,
            token: str | None = None) -> tuple[int, dict | str]:
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['X-Alas-Token'] = token
    payload = None if data is None else json.dumps(data).encode('utf-8')
    req = Request(base + path, data=payload, headers=headers)
    try:
        with urlopen(req, timeout=10) as response:
            body = response.read().decode('utf-8')
            return response.status, json.loads(body) if path != '/' else body
    except HTTPError as error:
        return error.code, json.loads(error.read().decode('utf-8'))


def wait_state(base: str, predicate, timeout: float = 30) -> dict:
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        status, document = request(base, '/api/state')
        if status == 200 and predicate(document):
            return document
        time.sleep(0.1)
    raise AssertionError('控制服务未在限时内进入预期状态')


def main() -> int:
    if not EXE.is_file():
        print('先构建 Release 版本再运行控制界面回归')
        return 1
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    with tempfile.TemporaryDirectory(prefix='alas-control-') as temp:
        root = Path(temp)
        output = (root / 'server.log').open('w', encoding='utf-8')
        process = subprocess.Popen(
            [str(EXE), 'control', '--port', str(port),
             '--workspace', str(root / 'workspace'),
             '--artifacts', str(root / 'runs')],
            cwd=ROOT, stdout=output, stderr=output,
        )
        try:
            until = time.monotonic() + 10
            while True:
                try:
                    state = wait_state(base, lambda _: True, timeout=1)
                    break
                except (AssertionError, URLError):
                    if process.poll() is not None or time.monotonic() >= until:
                        output.flush()
                        detail = (root / 'server.log').read_text(encoding='utf-8')[-1500:]
                        raise AssertionError(f'控制服务未启动: {detail}')
            token = state['token']
            assert state['active']['status'] == 'idle'
            status, page = request(base, '/')
            assert status == 200 and '<!doctype html>' in page.lower()

            status, _ = request(base, '/api/stop', data={})
            assert status == 403, '无令牌不能发停止请求'
            status, _ = request(base, '/api/report?stamp=..')
            assert status == 400, '历史报告路径不能越界'

            queue = {'tasks': [{'id': 'catalog', 'kind': 'task_catalog',
                                'input': {'limit': 1}}]}
            status, _ = request(base, '/api/queue', data={'queue': queue}, token=token)
            assert status == 200
            assert request(base, '/api/state')[1]['queue'] == queue
            status, _ = request(base, '/api/run', data={
                'queue': queue, 'mode': 'actions'}, token=token)
            assert status == 400, '动作运行必须有显式确认'
            assert not list((root / 'runs').iterdir()), '拒绝请求不能启动会话'

            status, _ = request(base, '/api/run', data={
                'queue': queue, 'mode': 'dry_run'}, token=token)
            assert status == 202
            # 新增的无关目录不能被误认成本次运行，即使它的名称排在真正运行之后。
            foreign = root / 'runs' / 'zzz-unrelated'
            foreign.mkdir()
            state = wait_state(base, lambda s: s['active']['status'] != 'running')
            report = state['report']
            assert Path(state['active']['run_directory']) != foreign
            assert Path(state['active']['run_directory']) == Path(report['directory'])
            assert report['queue_outcome'] == 'succeeded'
            assert report['device_configure_count'] == 0
            assert report['totals']['tasks'] == 1
            stamp = report['stamp']
            assert request(base, '/api/report?stamp=' + stamp)[1]['queue_outcome'] == 'succeeded'

            queue = {'tasks': [
                {'id': f'stage-{index}', 'kind': 'campaign_batch',
                 'input': {'chapters': [CHAPTER]}}
                for index in range(40)]}
            status, _ = request(base, '/api/run', data={
                'queue': queue, 'mode': 'dry_run'}, token=token)
            assert status == 202
            status, _ = request(base, '/api/stop', data={}, token=token)
            assert status == 200, '运行中可以请求停止'
            state = wait_state(base, lambda s: s['active']['status'] != 'running'
                               and s['active']['stop_requested'])
            report = state['report']
            assert report['queue_outcome'] == 'cancelled'
            assert report['stopped_early'] is True
            # 停止在任务边界生效；请求送达前可能已有少量 dry-run 任务完成。
            assert report['totals']['tasks'] == 40
            assert 0 < report['totals']['tasks_skipped'] <= 40
            assert report['totals']['tasks_failed'] == 0
            assert report['device_configure_count'] == 0
            assert report['evidence_complete'] is True
            print('控制界面回归通过：本机授权、队列保存、离线运行、报告与边界停止')
            return 0
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
            output.close()


if __name__ == '__main__':
    sys.exit(main())
