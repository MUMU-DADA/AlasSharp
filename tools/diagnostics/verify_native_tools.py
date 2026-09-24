"""Offline native tool dispatch and Core queue regression; no device or account config."""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import types
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
sys.path.insert(0, str(ROOT / 'tools'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
os.chdir(ENGINE)
import alas_vision as av
from alas import AzurLaneAutoScript
from module.exception import GameNotRunningError
from module.config.config import TaskEnd
from module.submodule.utils import get_available_func
from module.logger import logger


def host_checks(workspace):
    (workspace / 'config').mkdir()
    (workspace / 'config' / 'fixture.json').write_text('{"Alas":{}}', encoding='utf-8')
    shutil.copyfile(ENGINE / 'alas.py', workspace / 'alas.py')
    events = []
    state = {'mode': 'success'}
    original = object()
    class Device:
        _config = original

        @property
        def config(self):
            return self._config

        @config.setter
        def config(self, value):
            if state['mode'] == 'restore_failure' and value is original:
                raise RuntimeError('fixture config restore failure')
            self._config = value

        def stuck_record_clear(self):
            events.append('stuck')

        def click_record_clear(self):
            events.append('click')

        def screenshot(self):
            events.append('screenshot')

    device = Device()

    class Config:
        Error_SaveError = False
        Error_OnePushConfig = ''

        def __init__(self, config_name='alas', task=None):
            assert config_name == 'fixture' and task is None
            self.config_name = config_name
            events.append('config')

        def task_call(self, task):
            events.append(('task_call', task))

    def benchmark(config):
        events.append('benchmark')
        if state['mode'] == 'task_end':
            raise TaskEnd
        if state['mode'] == 'false':
            raise GameNotRunningError('fixture stopped')
        if state['mode'] == 'exit':
            raise SystemExit(0)
        if state['mode'] == 'exception':
            raise RuntimeError('fixture failure')

    class EventStory:
        def __init__(self, config, device, task):
            assert task == 'EventStory' and config is device.config
            events.append('story-init')

        def run(self):
            events.append('story-run')
            if state['mode'] == 'restore_failure':
                raise RuntimeError('fixture original story failure')

    def save_fixture_error(_):
        saved = workspace / 'log/error' / state['mode']
        saved.mkdir(parents=True, exist_ok=True)
        (saved / 'failure.png').write_bytes(b'synthetic-frame')
        (saved / 'log.txt').write_text('synthetic native log', encoding='utf-8')
        logger.warning('Saving error: ' + saved.relative_to(workspace).as_posix())

    def get_device(config=None):
        assert config.config_name == 'fixture'
        events.append('device')
        return device

    modules = {
        'module.daemon.benchmark': types.SimpleNamespace(run_benchmark=benchmark),
        'module.eventstory.eventstory': types.SimpleNamespace(EventStory=EventStory),
    }
    def run(task='Benchmark', **extra):
        return av.op_tool_run(dict(task=task, instance='fixture', allow_actions=True, device_configured=True,
                                   confirm=task, **extra))

    with patch.object(av, 'FORK', str(workspace)), patch.object(av, '_device_engine', get_device), \
         patch.object(av, '_DEVICE_ARGS', {'serial': 'fixture-device'}), \
         patch.dict(sys.modules, modules), patch('alas.AzurLaneConfig', Config), \
         patch('alas.handle_notify'), patch.object(AzurLaneAutoScript, 'save_error_log', save_fixture_error), \
         patch.object(logger, 'handlers', []), patch.object(logger, 'propagate', False):
        for task in get_available_func():
            assert av.op_tool_plan({'task': task})['found'] is True, task
        for task in ('loop', 'save_error_log', 'Reward', 'benchmark', '../Benchmark', None):
            assert av.op_tool_plan({'task': task})['found'] is False, task
        assert not events
        for values in ({'allow_actions': False}, {'allow_actions': 'true'}, {'confirm': 'wrong'},
                       {'instance': '../fixture'}, {'instance': 'missing'}, {'task': 'loop'}):
            request = dict(task='Benchmark', instance='fixture', allow_actions=True, confirm='Benchmark')
            request.update(values)
            assert av.op_tool_run(request)['decision'] == 'denied', values
        assert not events, events
        for mode, verdict in [('success', 'ran'), ('task_end', 'ran'), ('false', 'failed'),
                              ('exit', 'error'), ('exception', 'error')]:
            state['mode'] = mode
            events.clear()
            got = run()
            assert got['decision'] == verdict, got
            assert got['instance'] == 'fixture' and got['target']['method'] == 'benchmark'
            assert 'device' not in events and 'screenshot' not in events, events
            assert got['native_success'] is (verdict == 'ran'), got
            if mode == 'exception':
                assert got['traceback_tail'] and 'RuntimeError' in got['error'], got
                assert got['failure_frames'] == ['log/error/exception/failure.png']
                assert (workspace / got['failure_frames'][0]).is_file()
                assert got['native_error_log'] == 'log/error/exception/log.txt'
            if mode in ('false', 'exit', 'exception'):
                assert got['traceback_tail'] and all(line in got['error'] for line in got['traceback_tail']), got
                assert all('/' not in line and '\\' not in line for line in got['traceback_tail']), got
        state['mode'] = 'success'
        events.clear()
        got = run('EventStory')
        assert got['decision'] == 'ran', got
        assert not got.get('failure_frames') and not got.get('traceback_tail'), got
        assert events == ['config', 'device', 'stuck', 'click', 'story-init', 'story-run'], events
        assert device.config is original
        stale = av.op_tool_run(dict(task='EventStory', instance='fixture', allow_actions=True,
                                    confirm='EventStory', device_configured=False))
        assert stale['decision'] == 'error' and device.config is original, stale
        state['mode'] = 'restore_failure'
        got = run('EventStory')
        assert got['decision'] == 'error' and got['native_success'] is False
        assert 'SystemExit: 1' in got['error'] and 'RuntimeError' in got['error']
        assert 'fixture config restore failure' in got['error']
        assert any('run' in line for line in got['traceback_tail']) and any('config' in line for line in got['traceback_tail'])
        assert all(line in got['error'] for line in got['traceback_tail'])
        assert got['failure_frames'] == ['log/error/restore_failure/failure.png']
        state['mode'] = 'success'
        device.config = original
        with patch.object(av, '_DEVICE_ARGS', {}):
            state['mode'] = 'success'
            events.clear()
            assert run()['decision'] == 'ran' and 'device' not in events
            got = run('EventStory')
            assert got['decision'] == 'error' and 'device' not in events, got
    print('PASS: native registry, pre-construction gates, real tool methods/run(), lazy device and restoration')


def queue_checks(workspace):
    result = dict(task='Benchmark', instance='fixture', constructed=True, ran=True,
                  native_success=True, decision='ran', target={'method': 'benchmark'})
    base = dict(mode='queue', dry_run=False, allow_actions=True, artifacts=True,
                tasks=[dict(id='tool', kind='tool_run', required=True,
                            input=dict(task='Benchmark', instance='fixture',
                                       allow_actions=True, confirm='Benchmark'))],
                stub_responses={'tool_run': [{'result': result}]},
                expect=dict(outcome='succeeded', cleared=False,
                            tasks=[dict(id='tool', outcome='succeeded')]))
    cases = []
    for mode in ('success', 'dry_run', 'no_actions', 'denied', 'wrong_instance', 'missing_native_success', 'cancel'):
        case = copy.deepcopy(base)
        case['name'] = 'native_tool_' + mode
        if mode in ('dry_run', 'no_actions'):
            case['dry_run'] = mode == 'dry_run'
            case['allow_actions'] = False
            case['read_only_device'] = mode == 'no_actions'
        elif mode == 'denied':
            case['stub_responses']['tool_run'][0]['result']['decision'] = 'denied'
        elif mode == 'wrong_instance':
            case['stub_responses']['tool_run'][0]['result']['instance'] = 'another'
        elif mode == 'missing_native_success':
            del case['stub_responses']['tool_run'][0]['result']['native_success']
        elif mode == 'cancel':
            case['cancel_after_op'] = 'tool_run'
            case['tasks'].append(dict(id='later', kind='tool_run', input=copy.deepcopy(case['tasks'][0]['input'])))
            case['expect']['outcome'] = 'cancelled'
            case['expect']['tasks'].append(dict(id='later', outcome='skipped'))
        if mode not in ('success', 'cancel'):
            case['expect']['outcome'] = 'failed'
            case['expect']['tasks'][0]['outcome'] = 'failed'
        cases.append(case)
    fixture = workspace / 'tools.json'
    output = workspace / 'verdicts.json'
    fixture.write_text(json.dumps({'cases': cases}), encoding='utf-8')
    exe = ROOT / 'src/Alas.DataTool/bin/Release/net10.0/alashub.exe'
    completed = subprocess.run([str(exe), 'selftest-runtime', '--fixture', str(fixture), '--json', str(output),
                                '--workspace', str(workspace / 'runs')], capture_output=True,
                               text=True, encoding='utf-8', errors='replace', timeout=60)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    actual = json.loads(output.read_text(encoding='utf-8'))['cases']
    for case in actual:
        assert case['ok'], case
        if case['name'].endswith(('dry_run', 'no_actions')):
            assert not any(op.startswith('tool_run:') for op in case['backend_ops']), case
        for task in case['tasks']:
            assert Path(task['artifact']).is_file(), case
    print(f'PASS: Core tool queue ({len(cases)} cases), fail-closed evidence, cancellation and artifacts')


def main():
    root = ROOT / '.runtime' / 'verification'
    root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='native-tools-', dir=root) as folder:
        workspace = Path(folder)
        host_checks(workspace)
        queue_checks(workspace)


if __name__ == '__main__':
    main()
