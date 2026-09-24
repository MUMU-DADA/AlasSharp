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
import types
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime/engine'
sys.path.insert(0, str(ROOT / 'tools'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
os.chdir(ENGINE)
import alas_vision as host
from alas import AzurLaneAutoScript
from module.exception import GameNotRunningError
from module.logger import logger


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

        def get_next(self):
            command = commands[min(state['index'], len(commands) - 1)]
            return types.SimpleNamespace(command=command,
                next_run=datetime.now() + timedelta(seconds=60 if state['future'] else -60))

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
            state['count'] += 1
            events.append(('run', self.config.task.command))
            if mode in ('failure_retry', 'no_retry'):
                raise GameNotRunningError('fixture failure')
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
    with patch.object(host, 'FORK', str(root)), patch.object(host, '_DEVICE_ARGS', {'serial': 'fixture-device'}), \
         patch.object(host, '_device_engine', lambda config: device), patch('alas.AzurLaneConfig', Config), \
         patch.object(AzurLaneAutoScript, 'checker', property(lambda _: checker)), \
         patch('alas.handle_notify'), patch.dict(sys.modules, modules), \
         patch('module.base.resource.release_resources'), patch('alas.time.sleep'), \
         patch.object(logger, 'handlers', []), patch.object(logger, 'propagate', False), \
         patch.object(logger, 'set_file_logger'):
        args = dict(instance='fixture', allow_actions=True, confirm='fixture', artifact_directory=str(directory))
        for update in ({'allow_actions': False}, {'confirm': 'wrong'}, {'instance': '../fixture'}):
            bad = dict(args, **update)
            assert host.op_scheduler_run(bad)['decision'] == 'denied'
        assert not events
        got = host.op_scheduler_run(args)
    expected = 'error' if mode == 'failure_retry' else 'failed' if mode == 'no_retry' else 'stopped'
    assert got['decision'] == expected, (mode, got, events)
    assert device.config is initial_config, mode
    records = [json.loads(path.read_text(encoding='utf-8')) for path in sorted(directory.glob('dispatch-*.json'))]
    assert len(records) == state['count'] == got['dispatch_count'], (mode, got, records)
    assert all(record['finished_at'] and record['instance'] == 'fixture' for record in records)
    assert json.loads((directory / 'state.json').read_text(encoding='utf-8'))['phase'] == expected
    if mode == 'sequence':
        assert [record['scheduler_command'] for record in records] == ['Reward', 'Commission'], records
        assert ('delay', {'server_update': True}) in events and state['configs'] >= 3
    if mode == 'failure_retry':
        assert state['count'] == 3 and got['failed_dispatches'] == 3 and got['exit_code'] == '1'
        assert all(record['traceback_tail'] for record in records)
    if mode == 'no_retry':
        assert state['count'] == 1 and got['failed_dispatches'] == 1
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
        (workspace / 'config').mkdir()
        (workspace / 'config/fixture.json').write_text('{"Alas":{}}', encoding='utf-8')
        for mode in ('sequence', 'failure_retry', 'no_retry', 'wait_cancel', 'reload', 'recovered', 'prestop'):
            native_case(workspace, mode)
        queue_cases(workspace)


if __name__ == '__main__':
    main()
