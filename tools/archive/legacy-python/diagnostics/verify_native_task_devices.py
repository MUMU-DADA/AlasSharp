"""Exercise nested native planner/scanner factories through periodic and scheduler entries.

The native constructors, planner.run and dispatcher are retained. UI entry,
scanner I/O, calculator endpoints, config storage and physical devices are inert.
This verifies session reuse and task-boundary restoration, not island gameplay.
"""
from __future__ import annotations

from datetime import datetime, timedelta
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import alas_vision as host
import alas
import module.base.base as base
from module.device.device import Device
from module.island.order import IslandOrder
from module.island_handler.production_planner import IslandProductionPlanner
from module.island_handler.technology_scanner import IslandTechnologyScanner
from module.logger import logger


def exercise(root, entry, cached, fail, custom):
    directory = root / f'{entry}-{cached}-{fail}-{custom}'
    directory.mkdir()
    prior = object()
    device = object.__new__(Device)
    device.config = prior
    device.screenshot = lambda: None
    device.stuck_record_clear = lambda: None
    device.click_record_clear = lambda: None
    checker = lambda: 'previous checker'
    if custom:
        device.stuck_record_check = device.click_record_check = checker
    events, rogue = [], []

    class Config:
        Error_SaveError = False
        Error_OnePushConfig = ''
        Error_HandleError = False
        Optimization_WhenTaskQueueEmpty = 'stay_there'
        bound = {}
        is_actual_task = False

        def __init__(self, config_name='fixture', task=None):
            assert config_name == 'fixture'
            self.config_name = config_name
            self.task = SimpleNamespace(command=task or 'IslandOrder', next_run=datetime.now() - timedelta(minutes=1))
            self.pending_task, self.waiting_task, self.data = [self.task], [], {}

        def get_next(self):
            return self.task

        def bind(self, task):
            self.task = task

        def cross_get(self, key, default=None):
            if key.endswith('IslandTechnologyStatus'):
                return {} if cached else None
            return default

    def acquire(config=None):
        events.append('acquire')
        return device

    def forbidden_device(*args, **kwargs):
        rogue.append('physical constructor')
        raise AssertionError('Nested native module created a second device')

    def scan(scanner):
        assert scanner.device is device and scanner.config is device.config
        events.append('scan')
        if fail:
            raise RuntimeError('fixture nested scan failure')
        return {}

    def solve(**kwargs):
        events.append('solve')
        if fail:
            raise RuntimeError('fixture cached planner failure')

    def run_domain(domain):
        assert domain.device is device and domain.config is device.config
        for name in ('stuck_record_check', 'click_record_check'):
            assert (name in vars(device)) == custom, f'{name} leaked into next dispatch'
            if custom:
                assert vars(device)[name] is checker
        events.append('domain')
        planner = IslandProductionPlanner(domain.config, domain.device)
        planner.run(hard_floor_items_yaml={}, task_target_items={}, stuck_season_order_id=0, export=False)
        if entry == 'periodic' or events.count('domain') == 2:
            (directory / 'stop.request').write_text('stop', encoding='utf-8')

    original_alias = base.Device
    with patch.object(host, 'FORK', str(root)), \
            patch.object(host, '_DEVICE_ARGS', {'serial': 'fixture-device'}), \
            patch.object(host, '_device_engine', side_effect=acquire), \
            patch.object(host, 'op_periodic_plan', return_value=dict(
                found=True, task='IslandOrder', scheduler_command='IslandOrder', method='island_order')), \
            patch('alas.AzurLaneConfig', Config), patch('module.config.config.AzurLaneConfig', Config), \
            patch.object(Device, '__init__', side_effect=forbidden_device), \
            patch.object(base.ModuleBase, 'early_ocr_import'), \
            patch.object(IslandOrder, 'run', run_domain), \
            patch.object(IslandTechnologyScanner, 'get_technology_status', scan), \
            patch.object(IslandProductionPlanner, 'create_calculator', return_value=SimpleNamespace(
                solve_production_plan=solve, print_solved_production_plan=lambda: None)), \
            patch.object(alas.AzurLaneAutoScript, 'checker', property(lambda _: SimpleNamespace(
                wait_until_available=lambda: None, is_recovered=lambda: False, check_now=lambda: None))), \
            patch.object(alas.AzurLaneAutoScript, 'save_error_log'), patch('alas.handle_notify'), \
            patch.object(logger, 'handlers', []), patch.object(logger, 'set_file_logger'), \
            patch.object(logger, 'propagate', False):
        if entry == 'periodic':
            result = host.op_periodic_run(dict(instance='fixture', task='IslandOrder',
                confirm='IslandOrder', allow_actions=True))
        else:
            result = host.op_scheduler_run(dict(instance='fixture', confirm='fixture',
                allow_actions=True, artifact_directory=str(directory)))
    expected = 'error' if fail else 'ran' if entry == 'periodic' else 'stopped'
    assert not rogue, (entry, 'second device', result)
    assert result['decision'] == expected, result
    dispatches = 2 if entry == 'scheduler' and not fail else 1
    assert events.count('domain') == dispatches, events
    assert events.count('scan') == dispatches * int(not cached), events
    assert events.count('solve') == dispatches * int(cached or not fail), events
    assert device.config is prior, 'Device config leaked after task'
    for name in ('stuck_record_check', 'click_record_check'):
        assert (name in vars(device)) == custom, f'{name} instance override leaked'
        if custom:
            assert vars(device)[name] is checker, f'{name} previous override lost'
    assert base.Device is original_alias, 'Native ModuleBase factory not restored'
    if fail:
        assert result['traceback_tail'], result
    return dict(entry=entry, cached=cached, fail=fail, custom=custom, passed=True)


def main():
    output = ROOT / '.runtime/verification/native-task-devices'
    output.mkdir(parents=True, exist_ok=True)
    results = []
    with tempfile.TemporaryDirectory(prefix='scope-', dir=output) as temp:
        root = Path(temp)
        (root / 'config').mkdir()
        (root / 'config/fixture.json').write_text('{}', encoding='utf-8')
        for entry in ('periodic', 'scheduler'):
            for cached in (False, True):
                for fail in (False, True):
                    for custom in (False, True):
                        try:
                            results.append(exercise(root, entry, cached, fail, custom))
                        except AssertionError as error:
                            results.append(dict(entry=entry, cached=cached, fail=fail,
                                                custom=custom, passed=False, error=str(error)))
                            print(f'FAIL: {entry} cached={cached} error={fail} custom={custom}: {error}')
    (output / 'results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    passed = sum(item['passed'] for item in results)
    print(f'{passed}/{len(results)} native nested task device checks; no device or account access')
    return int(passed != len(results))


if __name__ == '__main__':
    raise SystemExit(main())
