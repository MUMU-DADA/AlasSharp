"""Validate one-shot overrides against native bound fields before device access.

Uses real generated defaults, native configuration binding and dispatch; the
terminal Reward method and physical device are inert. No account is read.
"""
from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
sys.stderr.reconfigure(encoding='utf-8', errors='replace')
import alas_vision as av
import alas
import module.config.config as config_module
import module.config.config_updater as updater_module
from module.config.config import AzurLaneConfig
from module.logger import logger
from module.reward.reward import Reward


class PeriodicOverrideTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='periodic-overrides-', dir=ROOT / '.runtime')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'config').mkdir()
        catalog = json.loads((Path(av.FORK) / 'module/config/argument/args.json').read_bytes())
        self.catalog = catalog
        defaults = {section: {group: {key: arg['value'] for key, arg in fields.items()}
                              for group, fields in groups.items()} for section, groups in catalog.items()}
        defaults['Alas']['Error'].update(SaveError=False, OnePushConfig='')
        self.file = self.root / 'config/fixture.json'
        self.file.write_text(json.dumps(defaults), encoding='utf-8')
        for rel in ('alas.py', 'module/config/argument/args.json'):
            target = self.root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((Path(av.FORK) / rel).read_bytes())

        def filepath(name, mod_name='alas'):
            if name != 'fixture' or mod_name != 'alas':
                raise AssertionError('Unexpected config access')
            return str(self.file)

        for context in (patch.object(av, 'FORK', str(self.root)),
                        patch.object(config_module, 'filepath_config', filepath),
                        patch.object(updater_module, 'filepath_config', filepath),
                        patch.object(logger, 'handlers', []), patch.object(logger, 'propagate', False),
                        patch.object(alas, 'handle_notify'),
                        patch.object(alas.AzurLaneAutoScript, 'save_error_log')):
            context.start()
            self.addCleanup(context.stop)
        # Let native migration complete once, then measure only operation effects.
        AzurLaneConfig('fixture', task='Reward')
        self.baseline = self.file.read_bytes()
        self.device_calls, self.observed = [], []
        prior = object()
        self.prior = prior

        class Device:
            config = prior
            def screenshot(inner):
                self.device_calls.append('screenshot')
            def stuck_record_clear(inner):
                pass
            def click_record_clear(inner):
                pass
        self.device = Device()

    def invoke(self, overrides):
        def acquire(config=None):
            self.device_calls.append('acquire')
            return self.device

        def endpoint(domain):
            self.observed.append({key: getattr(domain.config, key) for key in overrides})
            # In particular, the native business if-statement must never see a
            # string such as "false" as an enabled collection switch.
            self.observed[-1]['collect_oil_truth'] = bool(domain.config.Reward_CollectOil)

        with patch.object(av, '_device_engine', side_effect=acquire), \
                patch.object(Reward, 'run', endpoint), \
                patch('module.base.base.ModuleBase.EARLY_OCR_IMPORT', True):
            return av.op_periodic_run(dict(instance='fixture', task='Reward', confirm='Reward',
                                           allow_actions=True, overrides=overrides))

    def test_malformed_values_are_denied_before_device_acquisition(self):
        for key, value in [('Reward_CollectOil', 'false'), ('Reward_CollectOil', 0),
                           ('Reward_CollectOil', None), ('Reward_CollectOil', []),
                           ('Optimization_ScreenshotInterval', '0.3'),
                           ('Optimization_ScreenshotInterval', float('nan')),
                           ('Emulator_ControlMethod', 'not-a-backend'),
                           ('Scheduler_NextRun', '2026-02-30 00:00:00'),
                           ('Scheduler_Command', 'Research'),
                           ('not_a_bound_field', True)]:
            with self.subTest(field=key, value=value):
                self.device_calls.clear()
                self.observed.clear()
                self.file.write_bytes(self.baseline)
                result = self.invoke({key: value})
                self.assertEqual(result['decision'], 'denied', result)
                self.assertEqual(self.device_calls, [])
                self.assertEqual(self.observed, [])
                self.assertEqual(self.file.read_bytes(), self.baseline)
                self.assertIs(self.device.config, self.prior)

    def test_typed_values_follow_native_conversion_without_persistence(self):
        from datetime import datetime
        values = dict(Reward_CollectOil=False, Optimization_ScreenshotInterval=.3,
                      Scheduler_NextRun='2026-01-02 03:04:05')
        result = self.invoke(values)
        self.assertEqual(result['decision'], 'ran', result)
        self.assertEqual(len(self.observed), 1)
        observed = self.observed[0]
        self.assertIs(observed['Reward_CollectOil'], False)
        self.assertIs(observed['collect_oil_truth'], False)
        self.assertEqual(observed['Optimization_ScreenshotInterval'], .3)
        self.assertEqual(observed['Scheduler_NextRun'], datetime(2026, 1, 2, 3, 4, 5))
        self.assertEqual(self.file.read_bytes(), self.baseline)
        self.assertIs(self.device.config, self.prior)

    def test_mixed_override_is_atomic_before_execution(self):
        result = self.invoke(dict(Reward_CollectOil=False, Reward_CollectCoin='false'))
        self.assertEqual(result['decision'], 'denied', result)
        self.assertEqual(self.device_calls, [])
        self.assertEqual(self.observed, [])
        self.assertEqual(self.file.read_bytes(), self.baseline)

    def test_archived_real_overrides_still_validate_for_native_task_bindings(self):
        from native_task_overrides import validate_task_overrides
        from module.config.utils import parse_value
        verified = 0
        for path in sorted((ROOT / 'tools/diagnostics/queue-evidence').rglob('task-*.json')):
            request = json.loads(path.read_text(encoding='utf-8'))['input']
            overrides = request.get('overrides')
            if not overrides:
                continue
            command = av.op_periodic_plan(dict(task=request['task']))['scheduler_command']
            config = AzurLaneConfig('fixture', task=command)
            before = self.file.read_bytes()
            values = validate_task_overrides(config, overrides)
            for key, value in overrides.items():
                section, group, argument = config.bound[key].split('.')
                expected = parse_value(value, data=self.catalog[section][group][argument])
                self.assertEqual(values[key], expected, (command, key))
                self.assertIs(type(values[key]), type(expected), (command, key))
            self.assertEqual(self.file.read_bytes(), before)
            verified += 1
        self.assertGreater(verified, 0)
        print(f'PASS: {verified} archived override sets retain native bound types; no execution')

    def test_inherited_binding_and_unknown_descriptor_do_not_fall_back(self):
        from native_task_overrides import validate_task_overrides
        from module.api.protocol import ApiError
        config = AzurLaneConfig('fixture', task='OpsiExplore')
        selected = next(key for key, path in config.bound.items()
                        if path.startswith('OpsiGeneral.') and
                        self.catalog['OpsiGeneral'][path.split('.')[1]][path.split('.')[2]].get('type') == 'checkbox')
        descriptor = config.bound[selected]
        self.assertEqual(validate_task_overrides(config, {selected: False}), {selected: False})
        config.bound[selected] = 'Missing.Group.Argument'
        with self.assertRaises(ApiError):
            validate_task_overrides(config, {selected: False})
        config.bound[selected] = descriptor


if __name__ == '__main__':
    unittest.main(verbosity=2)
