"""Offline audit of cross-run native scheduler hoarding state.

Uses the current adapter and unchanged native Config, loop, get_next_task and run.
Only account storage, endpoint task, clock, device and wait boundary are mocked.
All output remains in the check's ignored verification directory.
"""
from __future__ import annotations

import ast
import copy
from contextlib import nullcontext
from datetime import datetime, timedelta
import importlib.util
import json
import sys
import types
from unittest.mock import patch

import verify_screenshot_auto as base

OUTPUT = base.ROOT / '.runtime/verification/scheduler-hoarding'
base.OUTPUT = OUTPUT
OUTPUT.mkdir(parents=True, exist_ok=True)
AzurLaneConfig = base.AzurLaneConfig
ConfigUpdater = base.ConfigUpdater
CLOCK = datetime(2030, 1, 2, 12, 0, 0)


class ClockMeta(type):
    def __instancecheck__(cls, value):
        return isinstance(value, datetime)


class FrozenClock(datetime, metaclass=ClockMeta):
    @classmethod
    def now(cls, tz=None):
        return CLOCK if tz is None else CLOCK.replace(tzinfo=tz)


class Capture:
    traceback_tail = []
    kind = None
    error_directory = None

    def __init__(self, *args, **kwargs):
        pass

    def flush_snapshot(self):
        pass

    def close(self):
        pass


def write_snapshot(directory, name, value):
    (directory / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


telemetry_tree = base.source(base.ROOT / 'tools/native_telemetry.py')
observe_node = next(n for n in telemetry_tree.body if isinstance(n, ast.FunctionDef) and n.name == 'observe_config')
telemetry_namespace = {}
exec(compile(ast.Module(body=[observe_node], type_ignores=[]), 'native_telemetry.py', 'exec'), telemetry_namespace)
sys.modules['native_telemetry'] = types.SimpleNamespace(
    NativeLogCapture=Capture, observe_config=telemetry_namespace['observe_config'], write_snapshot=write_snapshot)
sys.modules['module.api.config_service'] = types.SimpleNamespace(validate_name=lambda name: name)
sys.modules['module.notify'] = types.SimpleNamespace(handle_notify=lambda *a, **kw: None)
sys.modules['module.base.resource'] = types.SimpleNamespace(release_resources=lambda **kw: None)

import alas
import module.config.config as config_module
from module.config.config_generated import GeneratedConfig

base.source(base.ENGINE / 'alas.py')
base.source(base.ENGINE / 'module/config/config_generated.py')
scheduler_source = base.ROOT / 'tools/native_scheduler.py'
base.source(scheduler_source)
spec = importlib.util.spec_from_file_location('native_scheduler_audit_subject', scheduler_source)
scheduler = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scheduler)


class InertDevice:
    def __init__(self, config):
        self.config = config

    def screenshot(self):
        pass

    def release_during_wait(self):
        pass

    def stuck_record_clear(self):
        pass

    def click_record_clear(self):
        pass


class RunFixture:
    def __init__(self, name, overdue_minutes):
        self.store = base.MemoryStore()
        self.events = []
        self.config_ids = []
        self.class_ids = []
        self.directory = OUTPUT / 'hoarding-fixture' / name
        self.directory.mkdir(parents=True, exist_ok=True)
        for name in ('stop.request', 'state.json', 'dispatch-000001.json'):
            generated = self.directory / name
            if generated.exists():
                generated.unlink()
        for task in self.store.data.values():
            if 'Scheduler' in task:
                task['Scheduler'].update(Enable=False, NextRun=CLOCK + timedelta(hours=1))
        self.store.data['Reward']['Scheduler'].update(
            Enable=True, Command='Reward', NextRun=CLOCK - timedelta(minutes=overdue_minutes))
        self.store.data['Alas']['Optimization'].update(TaskHoardingDuration=10, WhenTaskQueueEmpty='stay_there')
        self.device = None
        self.marker_root = OUTPUT / 'hoarding-fixture' / 'synthetic-engine'
        (self.marker_root / 'config').mkdir(parents=True, exist_ok=True)
        (self.marker_root / 'config/fixture.json').write_text('{}', encoding='utf-8')

    def engine(self, config):
        self.config_ids.append(id(config))
        self.class_ids.append(id(type(config)))
        if self.device is None:
            self.device = InertDevice(config)
        return self.device

    def stop(self):
        (self.directory / 'stop.request').write_text('offline audit boundary', encoding='utf-8')

    def reward(self, runner):
        self.events.append(dict(action='dispatch', command='Reward', now=str(CLOCK),
                                hoarding=AzurLaneConfig.is_hoarding_task))
        self.stop()

    def wait(self, runner, future):
        self.events.append(dict(action='wait_until', future=str(future),
                                delay_seconds=(future - CLOCK).total_seconds(),
                                hoarding=AzurLaneConfig.is_hoarding_task))
        self.stop()
        assert runner.stop_event.is_set()
        raise SystemExit(0)

    def run(self):
        host = types.SimpleNamespace(FORK=self.marker_root, _DEVICE_ARGS={'serial': 'fixture-device'},
                                     _device_engine=self.engine, _LoggedNativeFailure=Capture,
                                     native_task_runtime=lambda **kw: nullcontext())
        checker = types.SimpleNamespace(wait_until_available=lambda: None, is_recovered=lambda: False)
        before = AzurLaneConfig.is_hoarding_task
        with patch.object(ConfigUpdater, 'args', property(lambda _: base.catalog)), \
                patch.object(ConfigUpdater, 'read_file', lambda _, *a, **kw: self.store.read(*a, **kw)), \
                patch.object(ConfigUpdater, 'write_file', staticmethod(self.store.write)), \
                patch.object(config_module, 'datetime', FrozenClock), \
                patch.object(alas, 'datetime', FrozenClock), \
                patch.object(alas.AzurLaneAutoScript, 'checker', property(lambda _: checker)), \
                patch.object(alas.AzurLaneAutoScript, 'reward', lambda runner: self.reward(runner)), \
                patch.object(alas.AzurLaneAutoScript, 'wait_until', lambda runner, future: self.wait(runner, future)):
            result = scheduler.run_scheduler(dict(instance='fixture', allow_actions=True, confirm='fixture',
                                                  artifact_directory=str(self.directory)), host)
        return dict(class_state_before=before, class_state_after=AzurLaneConfig.is_hoarding_task,
                    events=self.events, result=result, distinct_configs=len(set(self.config_ids)),
                    native_config_class_identity_preserved=all(x == id(AzurLaneConfig) for x in self.class_ids))


def main():
    initial = AzurLaneConfig.is_hoarding_task
    assert initial is True, 'Run this verifier in a fresh Python process'
    cases = {}
    try:
        first = RunFixture('first-overdue-20-minutes', 20).run()
        second = RunFixture('second-overdue-5-minutes', 5).run()
        cases.update(first=first, second=second)
        assert first['class_state_before'] is first['class_state_after'] is True, first
        assert first['events'][0]['action'] == 'dispatch' and first['result']['dispatch_count'] == 1, first
        assert second['events'][0]['action'] == 'wait_until', second
        assert second['events'][0]['delay_seconds'] == 300, second
        assert second['result']['dispatch_count'] == 0, second
        assert all(case['result']['decision'] == 'stopped' for case in cases.values()), cases

        # A previous operation may leave False. Each scheduler starts with the
        # native fresh-process value and restores the exact caller state.
        AzurLaneConfig.is_hoarding_task = False
        prior_false = RunFixture('prior-false', 5).run()
        assert prior_false['class_state_before'] is prior_false['class_state_after'] is False, prior_false
        assert prior_false['events'][0]['action'] == 'wait_until', prior_false
        assert prior_false['events'][0]['delay_seconds'] == 300, prior_false
        cases['prior_false'] = prior_false

        # Restore on an escaping native task failure, not just a stop boundary.
        for previous in (False, True):
            AzurLaneConfig.is_hoarding_task = previous
            fixture = RunFixture('failure-' + str(previous), 20)
            fixture.store.data['Alas']['Error']['HandleError'] = False
            def fail(runner):
                raise RuntimeError('synthetic scheduler failure')
            fixture.reward = fail
            failed = fixture.run()
            assert failed['result']['decision'] == 'error', failed
            assert failed['class_state_after'] is previous, failed
            assert failed['result']['traceback_tail'], failed
            cases['failure_' + str(previous)] = failed
    finally:
        AzurLaneConfig.is_hoarding_task = initial
    assert not base.IO['blocked_io'], base.IO
    report = dict(passed=True, cases=cases, io=base.IO, source_sha256=base.SOURCE_HASHES,
                  limits=['No device, real wait, network or account access.',
                          'Native Config selection, loop and dispatch wrapper executed unchanged.'])
    (OUTPUT / 'results.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('5/5 native scheduler hoarding lifetime checks passed; native 300-second wait observed without sleeping.')


if __name__ == '__main__':
    main()
