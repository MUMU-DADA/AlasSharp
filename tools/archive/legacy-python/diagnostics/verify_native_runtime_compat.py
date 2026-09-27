"""Check shared native dispatch runtime compatibility in fresh offline processes."""
from __future__ import annotations

import argparse
from datetime import datetime, timedelta
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))


def exercise(entry):
    import alas_vision as av
    import numpy as np
    from alas import AzurLaneAutoScript
    from module.map_detection.utils import Lines, Points
    from module.logger import logger

    logger.setLevel(50)
    # A prior explicit all-clear sortie must not force an unrelated task to use
    # that option. The original fast-forward method returns before any UI work
    # when this synthetic map has no clear mode.
    av.apply_clear_all_override(True)
    with tempfile.TemporaryDirectory(prefix='native-runtime-') as temporary:
        root = Path(temporary)
        (root / 'config').mkdir()
        (root / 'config/fixture.json').write_text('{}', encoding='utf-8')
        artifacts = root / 'artifacts'
        artifacts.mkdir()
        events, geometry_errors, inject_error = [], [], [False]
        initial = object()
        device = SimpleNamespace(config=initial, screenshot=lambda: events.append('frame'),
                                 stuck_record_clear=lambda: None, click_record_clear=lambda: None)

        class Config:
            Error_SaveError = False
            Error_OnePushConfig = ''
            Error_HandleError = False
            Optimization_WhenTaskQueueEmpty = 'stay_there'
            bound = {}

            def __init__(self, config_name='fixture', task=None):
                assert config_name == 'fixture'
                self.config_name = config_name
                self.task = SimpleNamespace(command=task or 'Reward', next_run=datetime.now() - timedelta(seconds=10))
                self.pending_task, self.waiting_task, self.data = [self.task], [], {}

            def get_next(self):
                return self.task

            def bind(self, task):
                self.task = task

        def geometry():
            # These are genuine native calculations, not calls to a patched stub.
            horizontal = Lines([[10, np.pi / 2], [20, np.pi / 2]], is_horizontal=True)
            vertical = Lines([[30, 0], [40, 0]], is_horizontal=False)
            try:
                actual = horizontal.cross(vertical)
            except Exception as error:
                geometry_errors.append(f'{type(error).__name__}: {error}')
                raise
            np.testing.assert_allclose(actual.points, [[30, 10], [40, 10], [30, 20], [40, 20]])
            empty = Points([])
            assert not empty and empty.x.size == empty.y.size == 0
            from module.handler.fast_forward import FastForwardHandler
            state = SimpleNamespace(map_has_clear_mode=False,
                                    config=SimpleNamespace(MAP_CLEAR_ALL_THIS_TIME=False))
            FastForwardHandler.handle_fast_forward(state)
            assert state.config.MAP_CLEAR_ALL_THIS_TIME is False, 'Previous sortie clear_all leaked into native task'
            events.append('native-geometry')
            if inject_error[0]:
                raise RuntimeError('fixture native failure after compatibility setup')
            (artifacts / 'stop.request').write_text('stop', encoding='utf-8')

        plan = dict(found=True, method='reward', scheduler_command='Reward')
        with patch.object(av, 'FORK', str(root)), \
                patch.object(av, '_DEVICE_ARGS', {'serial': 'fixture-device'}), \
                patch.object(av, '_device_engine', return_value=device), \
                patch.object(av, 'op_periodic_plan', return_value=plan), \
                patch.object(av, 'op_tool_plan', return_value=dict(found=True, method='benchmark')), \
                patch('module.config.config.AzurLaneConfig', Config), patch('alas.AzurLaneConfig', Config), \
                patch.object(AzurLaneAutoScript, 'reward', lambda self: geometry()), \
                patch.object(AzurLaneAutoScript, 'benchmark', lambda self: geometry()), \
                patch.object(AzurLaneAutoScript, 'checker', property(lambda self: SimpleNamespace(
                    wait_until_available=lambda: None, is_recovered=lambda: False, check_now=lambda: None))), \
                patch.object(AzurLaneAutoScript, 'save_error_log'), patch('alas.handle_notify'), \
                patch.object(logger, 'handlers', []), patch.object(logger, 'set_file_logger'):
            operation, request = {
                'periodic': (av.op_periodic_run, dict(instance='fixture', task='Reward', confirm='Reward', allow_actions=True)),
                'scheduler': (av.op_scheduler_run, dict(instance='fixture', confirm='fixture', allow_actions=True,
                                                      artifact_directory=str(artifacts))),
                'tool': (av.op_tool_run, dict(instance='fixture', task='Benchmark', confirm='Benchmark',
                                            allow_actions=True, device_configured=True)),
            }[entry]
            with patch.object(av, 'prepare_native_runtime', side_effect=AssertionError('Gate must precede initialization')):
                denied = operation(dict(request, allow_actions=False))
                assert denied['decision'] == 'denied' and not events
            for inject_error[0] in (False, True):
                (artifacts / 'stop.request').unlink(missing_ok=True)
                result = operation(request)
                expected = 'error' if inject_error[0] else 'stopped' if entry == 'scheduler' else 'ran'
                assert result['decision'] == expected, (result, geometry_errors)
                if entry == 'scheduler':
                    assert result['dispatch_count'] == 1, result
                if entry == 'tool':
                    assert 'frame' not in events, 'Device-free tools must stay lazy'
                assert av._CLEAR_ALL_OVERRIDE['enabled'] is True, 'Override scope not restored'
                if inject_error[0]:
                    assert result['traceback_tail'], result
        assert events.count('native-geometry') == 2
        assert device.config is initial
        assert av._CLEAR_ALL_OVERRIDE['enabled'] is True, 'Dispatch must restore the previous override scope'
    print(f'PASS: {entry} fresh-process geometry, denied-call purity and success/error override restoration')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--entry', choices=('periodic', 'scheduler', 'tool'))
    args = parser.parse_args()
    if args.entry:
        exercise(args.entry)
        return 0
    results = []
    output = ROOT / '.runtime/verification/native-runtime-compat'
    output.mkdir(parents=True, exist_ok=True)
    for entry in ('periodic', 'scheduler', 'tool'):
        result = subprocess.run([sys.executable, str(Path(__file__).resolve()), '--entry', entry],
                                capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=60)
        (output / f'{entry}.log').write_text(result.stdout + '\n' + result.stderr, encoding='utf-8')
        results.append(dict(entry=entry, ok=result.returncode == 0))
        print(f'{entry}: {"PASS" if result.returncode == 0 else "FAIL"}')
    (output / 'results.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    return int(not all(row['ok'] for row in results))


if __name__ == '__main__':
    raise SystemExit(main())
