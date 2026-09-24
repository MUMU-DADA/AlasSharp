"""Real upstream loop/get_next_task/wait_until/run with synthetic config/device, plus Core artifacts."""
from __future__ import annotations

import copy
from datetime import datetime, timedelta
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import types
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime/engine'
sys.path.insert(0, str(ROOT / 'tools'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
os.chdir(ENGINE)
import alas_vision as host
from alas import AzurLaneAutoScript
from module.exception import GameNotRunningError, MapDetectionError
from module.logger import logger
import native_telemetry


def snapshot_io_cases(root):
    """Reproduce Windows sharing failures with real handles, no device I/O."""
    directory = root / 'snapshot-io'
    directory.mkdir()
    target = directory / 'state.json'
    native_telemetry.write_snapshot(directory, target.name, {'before': True})
    original = target.read_bytes()
    # Unrelated filesystem errors must not be retried or erase the prior file.
    with patch.object(Path, 'replace', side_effect=OSError('fixture full disk')) as replace, \
            patch.object(native_telemetry, '_replacement_pause') as pause:
        try:
            native_telemetry.write_snapshot(directory, target.name, {'after': True})
        except OSError:
            pass
        else:
            raise AssertionError('permanent write error was swallowed')
        assert replace.call_count == 1 and not pause.called
    assert target.read_bytes() == original
    if os.name != 'nt':
        print('SKIP: real Windows sharing-lock cases require Windows')
        return

    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL

    def lock_target():
        # Read and write sharing allowed; delete sharing intentionally omitted.
        handle = kernel.CreateFileW(str(target), 0x80000000, 3, None, 3, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        return handle

    handle = lock_target()
    timer = threading.Timer(.06, lambda: kernel.CloseHandle(handle))
    timer.start()
    try:
        native_telemetry.write_snapshot(directory, target.name, {'after': True})
    finally:
        timer.join()
    assert json.loads(target.read_text(encoding='utf-8')) == {'after': True}
    before_locked = target.read_bytes()
    handle = lock_target()
    try:
        try:
            native_telemetry.write_snapshot(directory, target.name, {'blocked': True})
        except PermissionError:
            pass
        else:
            raise AssertionError('persistent sharing lock was swallowed')
        assert target.read_bytes() == before_locked
        assert json.loads((directory / 'state.json.tmp').read_text(encoding='utf-8')) == {'blocked': True}
    finally:
        kernel.CloseHandle(handle)
    print('PASS: snapshot transient sharing lock recovered; permanent lock/error preserves evidence and fails')


def native_case(root, mode):
    directory = root / mode
    directory.mkdir()
    stop = directory / 'stop.request'
    events = []
    state = dict(index=0, configs=0, future=mode in ('wait_cancel', 'reload'),
                 recovered=mode == 'recovered', count=0)
    commands = ['Restart', 'Reward', 'Commission'] if mode == 'sequence' else ['Reward']
    initial_config = object()

    class Config:
        Error_SaveError = False
        Error_OnePushConfig = ''
        Optimization_WhenTaskQueueEmpty = 'stay_there'
        Error_HandleError = mode != 'no_retry'

        def __init__(self, config_name='alas'):
            assert config_name == 'fixture'
            self.config_name = config_name
            state['configs'] += 1
            self.data = {'Dashboard': {'Oil': {'Value': 1234, 'Limit': 25000,
                                             'Record': '2026-01-02 03:04:05'}}}
            self.pending_task = []
            self.waiting_task = []

        def get_next(self):
            command = commands[min(state['index'], len(commands) - 1)]
            task = types.SimpleNamespace(command=command,
                next_run=datetime.now() + timedelta(seconds=60 if state['future'] else -60))
            self.pending_task = [] if state['future'] else [task]
            self.waiting_task = [task] if state['future'] else []
            return task

        def bind(self, task):
            events.append(('bind', task.command))

        def task_delay(self, **kwargs):
            events.append(('delay', kwargs))
            state['index'] += 1

        def task_call(self, task):
            events.append(('task_call', task))

        def start_watching(self):
            events.append('watch')

        def should_reload(self):
            state['future'] = False
            return True

    class Device:
        config = initial_config

        def screenshot(self):
            events.append('screenshot')

        def stuck_record_clear(self):
            events.append('stuck')

        def click_record_clear(self):
            events.append('click')

        def release_during_wait(self):
            events.append('release')
            if mode == 'wait_cancel':
                stop.write_text('stop')

    class Reward:
        def __init__(self, config, device):
            assert config is device.config
            self.config = config

        def run(self):
            snapshot = json.loads((directory / 'state.json').read_text(encoding='utf-8'))
            assert snapshot['phase'] == 'running' and snapshot['task'] == self.config.task.command
            assert [task['name'] for task in snapshot['pending']] == [self.config.task.command]
            assert snapshot['resources'][0]['value'] == 1234
            logger.warning('fixture native warning')
            logger.print('fixture rich output')
            state['count'] += 1
            events.append(('run', self.config.task.command))
            if mode in ('failure_retry', 'no_retry'):
                raise GameNotRunningError('fixture failure')
            if mode == 'fatal_map':
                raise MapDetectionError('fixture map detection failure')
            state['index'] += 1
            if mode != 'sequence' or state['index'] >= len(commands):
                stop.write_text('stop')
                # Signal requests never throw into an in-progress native method.
                events.append('native-return-boundary')

    class Checker:
        def wait_until_available(self):
            events.append('server-wait')

        def is_recovered(self):
            value = state['recovered']
            state['recovered'] = False
            return value

        def check_now(self):
            events.append('server-check')

    device = Device()
    checker = Checker()
    modules = {'module.reward.reward': types.SimpleNamespace(Reward=Reward),
               'module.commission.commission': types.SimpleNamespace(RewardCommission=Reward)}
    if mode == 'prestop':
        stop.write_text('stop')
    native_write = native_telemetry.write_snapshot
    captures = []
    native_close = native_telemetry.NativeLogCapture.close

    def write_with_final_failure(directory, name, value):
        if mode == 'artifact_failure' and name == 'logs.json' and stop.is_file():
            raise OSError('fixture final log write failure')
        return native_write(directory, name, value)

    def track_close(capture):
        captures.append(capture)
        return native_close(capture)

    def save_fixture_error(_):
        saved = root / 'log/error' / mode
        saved.mkdir(parents=True, exist_ok=True)
        (saved / 'failure.png').write_bytes(b'synthetic-frame')
        (saved / 'log.txt').write_text('synthetic native log', encoding='utf-8')
        logger.warning('Saving error: ' + saved.relative_to(root).as_posix())

    with patch.object(host, 'FORK', str(root)), patch.object(host, '_DEVICE_ARGS', {'serial': 'fixture-device'}), \
         patch.object(host, '_device_engine', lambda config: device), patch('alas.AzurLaneConfig', Config), \
         patch.object(AzurLaneAutoScript, 'checker', property(lambda _: checker)), \
         patch('alas.handle_notify'), patch.dict(sys.modules, modules), \
         patch('module.base.resource.release_resources'), patch('alas.time.sleep'), \
         patch.object(logger, 'handlers', []), patch.object(logger, 'propagate', False), \
         patch.object(logger, 'set_file_logger'), \
         patch.object(AzurLaneAutoScript, 'save_error_log', save_fixture_error), \
         patch.object(native_telemetry, 'write_snapshot', side_effect=write_with_final_failure), \
         patch.object(native_telemetry.NativeLogCapture, 'close', track_close):
        args = dict(instance='fixture', allow_actions=True, confirm='fixture', artifact_directory=str(directory))
        for update in ({'allow_actions': False}, {'confirm': 'wrong'}, {'instance': '../fixture'}):
            bad = dict(args, **update)
            assert host.op_scheduler_run(bad)['decision'] == 'denied'
        assert not events
        got = host.op_scheduler_run(args)
        assert not logger.handlers, 'scheduler telemetry handler leaked'
    expected = 'error' if mode in ('failure_retry', 'artifact_failure', 'fatal_map') else 'failed' if mode == 'no_retry' else 'stopped'
    assert got['decision'] == expected, (mode, got, events)
    assert device.config is initial_config, mode
    records = [json.loads(path.read_text(encoding='utf-8')) for path in sorted(directory.glob('dispatch-*.json'))]
    assert len(records) == state['count'] == got['dispatch_count'], (mode, got, records)
    assert all(record['finished_at'] and record['instance'] == 'fixture' for record in records)
    assert json.loads((directory / 'state.json').read_text(encoding='utf-8'))['phase'] == expected
    logs = json.loads((directory / 'logs.json').read_text(encoding='utf-8'))
    complete = [json.loads(line) for line in (directory / 'native-log.jsonl').read_text(encoding='utf-8').splitlines()]
    assert logs['instance'] == 'fixture'
    if mode == 'artifact_failure':
        assert got['artifact_errors'] and got['traceback_tail'] and 'fixture final log write failure' in got['error']
        assert captures and all(capture._output.closed for capture in captures)
        assert logs['cursor'] <= len(complete), 'stale snapshot cannot invent raw entries'
    else:
        assert logs['cursor'] == len(complete)
    assert [entry['id'] for entry in complete] == list(range(1, len(complete) + 1))
    if records:
        assert any(entry['level'] == 'WARNING' and 'fixture native warning' in entry['message'] for entry in complete)
        assert any('fixture rich output' in entry['message'] for entry in complete)
    if mode == 'wait_cancel':
        snapshot = json.loads((directory / 'state.json').read_text(encoding='utf-8'))
        assert not snapshot['pending'] and snapshot['waiting'][0]['name'] == 'Reward'
    if mode == 'sequence':
        assert [record['scheduler_command'] for record in records] == ['Reward', 'Commission'], records
        assert ('delay', {'server_update': True}) in events and state['configs'] >= 3
    if mode == 'failure_retry':
        assert state['count'] == 3 and got['failed_dispatches'] == 3 and got['exit_code'] == '1'
        assert all(record['traceback_tail'] for record in records)
        assert all(all(line in record['error'] for line in record['traceback_tail']) for record in records), records
        assert 'GameNotRunningError' in got['error'] and got['failure_frames'] == [], got
        assert got['traceback_tail'] and all(line in got['error'] for line in got['traceback_tail']), got
    if mode == 'no_retry':
        assert state['count'] == 1 and got['failed_dispatches'] == 1
    if mode == 'fatal_map':
        frame = 'log/error/fatal_map/failure.png'
        assert got['exit_code'] == '1' and state['count'] == 1 and got['last_failed_dispatch'] == 1, got
        assert got['failure_frames'] == records[0]['failure_frames'] == [frame]
        assert (root / frame).is_file() and got['native_error_log'] == 'log/error/fatal_map/log.txt'
        assert 'MapDetectionError' in got['error'] and 'SystemExit: 1' in got['error'], got
        assert all(line in got['error'] for line in got['traceback_tail'])
        assert all('/' not in line and '\\' not in line for line in got['traceback_tail'])
    elif mode not in ('failure_retry', 'no_retry'):
        assert 'last_failed_dispatch' not in got and got['failure_frames'] == [], got
    if mode == 'wait_cancel':
        assert not records and got['exit_code'] == '0' and 'watch' in events
    if mode == 'reload':
        assert state['configs'] >= 2 and 'watch' in events
    if mode == 'recovered':
        assert ('task_call', 'Restart') in events
    if mode == 'prestop':
        assert not events and not records
    print('PASS: native scheduler ' + mode)


def queue_cases(workspace):
    response = dict(instance='fixture', decision='stopped', stop_observed=True,
                    constructed=True, ran=True, dispatch_count=2, failed_dispatches=0)
    base = dict(name='scheduler_stop', dry_run=False, allow_actions=True, artifacts=True, serial='fixture-device',
                tasks=[dict(id='scheduler', kind='scheduler_run', required=True,
                            input=dict(instance='fixture', allow_actions=True, confirm='fixture'))],
                stub_responses={'scheduler_run': [{'result': response}]}, cancel_after_op='scheduler_run',
                expect=dict(outcome='cancelled', tasks=[dict(id='scheduler', outcome='skipped', error_kind='cancelled')]))
    cases = []
    for mode in ('stop', 'dry_run', 'wrong_confirm', 'no_artifacts', 'upstream_failure', 'unobserved_stop'):
        case = copy.deepcopy(base)
        case['name'] = 'scheduler_' + mode
        if mode != 'stop':
            case.pop('cancel_after_op')
            case['expect'] = dict(outcome='failed', tasks=[dict(id='scheduler', outcome='failed')])
        if mode == 'dry_run':
            case['dry_run'] = True
        elif mode == 'wrong_confirm':
            case['tasks'][0]['input']['confirm'] = 'another'
        elif mode == 'no_artifacts':
            case['artifacts'] = False
        elif mode == 'upstream_failure':
            case['stub_responses']['scheduler_run'][0]['result']['decision'] = 'error'
        elif mode == 'unobserved_stop':
            case['cancel_after_op'] = 'scheduler_run'
            case['stub_responses']['scheduler_run'][0]['result']['stop_observed'] = False
        cases.append(case)
    fixture, output = workspace / 'fixture.json', workspace / 'results.json'
    fixture.write_text(json.dumps({'cases': cases}), encoding='utf-8')
    exe = ROOT / 'src/Alas.DataTool/bin/Release/net10.0/alashub.exe'
    proc = subprocess.run([str(exe), 'selftest-runtime', '--fixture', str(fixture), '--json', str(output),
                           '--workspace', str(workspace / 'runs')], capture_output=True,
                          text=True, encoding='utf-8', errors='replace', timeout=60)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    actual = json.loads(output.read_text(encoding='utf-8'))['cases']
    for case in actual:
        assert case['ok'], case
        if case['name'] in ('scheduler_dry_run', 'scheduler_wrong_confirm', 'scheduler_no_artifacts'):
            assert not any(op.startswith('scheduler_run:') for op in case['backend_ops']), case
        if case['name'] == 'scheduler_stop':
            artifact = json.loads(Path(case['tasks'][0]['artifact']).read_text(encoding='utf-8'))
            path = Path(case['run_directory']) / artifact['evidence']['artifacts'] / 'stop.request'
            assert path.is_file(), path
    print('PASS: Core scheduler queue (6 cases), cancellation marker, authorization and evidence')


def main():
    root = ROOT / '.runtime/verification'
    root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='scheduler-', dir=root) as folder:
        workspace = Path(folder)
        snapshot_io_cases(workspace)
        (workspace / 'config').mkdir()
        (workspace / 'config/fixture.json').write_text('{"Alas":{}}', encoding='utf-8')
        from native_telemetry import NativeLogCapture
        telemetry = workspace / 'telemetry'
        telemetry.mkdir()
        handler = NativeLogCapture(telemetry, 'fixture')
        with patch.object(logger, 'handlers', [handler]), patch.object(logger, 'propagate', False):
            for index in range(405):
                logger.info('log %s', index)
            logger.print('x' * 13000)
            handler.close()
        tail = json.loads((telemetry / 'logs.json').read_text(encoding='utf-8'))
        raw = [json.loads(line) for line in (telemetry / 'native-log.jsonl').read_text(encoding='utf-8').splitlines()]
        assert len(raw) == 406 and len(tail['entries']) == 400 and tail['cursor'] == 406
        assert len(tail['entries'][-1]['message']) == 12000 and len(raw[-1]['message']) >= 13000
        print('PASS: native log ring is bounded, cursors stable, full raw evidence retained')
        for mode in ('sequence', 'failure_retry', 'no_retry', 'wait_cancel', 'reload', 'recovered', 'prestop',
                     'artifact_failure', 'fatal_map', 'sequence_after_failure'):
            native_case(workspace, mode)
        queue_cases(workspace)


if __name__ == '__main__':
    main()
