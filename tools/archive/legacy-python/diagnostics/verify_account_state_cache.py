"""Verify frame provenance at native task boundaries without device actions."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys

import dotnet_env  # noqa: E402  （同目录的 .NET 环境解析）
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import alas_vision as av


class CachedAccountStateTests(unittest.TestCase):
    def setUp(self):
        self.saved = dict(av._state)
        self.old = np.full((8, 8, 3), 11, dtype=np.uint8)
        self.current = np.full((8, 8, 3), 42, dtype=np.uint8)
        av._state.update(image=self.old, image_path='offline-old-frame.png')
        self.config = SimpleNamespace(config_name='selected-instance')
        self.device = SimpleNamespace(image=self.current, has_cached_image=True, config=self.config)
        self.observed = []

    def tearDown(self):
        av._state.clear()
        av._state.update(self.saved)

    def read(self, args, device=None):
        def pages(_):
            value = int(av._state['image'][0, 0, 0])
            self.observed.append(value)
            return dict(hit=[f'fixture_frame_{value}'], errors=[])
        with patch.object(av, '_DEVICE_OBJ', self.device if device is None else device), \
                patch.object(av, '_device_engine', side_effect=AssertionError('No device acquisition permitted')), \
                patch.object(av, 'op_page_current', side_effect=pages), \
                patch.object(av, 'op_appear_on', return_value=dict(appear=False, color=[], expected=[], tolerance=99)), \
                patch.object(av, '_map_config', return_value=SimpleNamespace(config_name='offline-default')):
            return av.op_account_state(args)

    def test_device_boundary_uses_native_cached_frame_and_bound_config(self):
        result = self.read(dict(device_cached=True))
        self.assertEqual(result['pages'], ['fixture_frame_42'])
        self.assertEqual(result['frame']['source'], 'device_cached')
        self.assertIsNone(result['frame']['path'])
        self.assertEqual(result['config_name'], 'selected-instance')
        self.assertEqual(self.observed, [42])

    def test_no_cached_frame_never_falls_back_to_old_host_image(self):
        for device in (SimpleNamespace(image=None, has_cached_image=False),
                       SimpleNamespace(image=self.current, has_cached_image=False)):
            av._state.update(image=self.old, image_path='offline-old-frame.png')
            result = self.read(dict(device_cached=True), device)
            self.assertFalse(result['frame']['available'])
            self.assertEqual(result['frame']['source'], 'device_cached')
            self.assertIn('error', result)
            self.assertIsNone(av._state['image'])
        self.assertEqual(self.observed, [])

    def test_uninitialized_device_is_not_created(self):
        with patch.object(av, '_DEVICE_OBJ', None), \
                patch.object(av, '_device_engine', side_effect=AssertionError('No device acquisition permitted')):
            result = av.op_account_state(dict(device_cached=True))
        self.assertFalse(result['frame']['available'])
        self.assertEqual(result['frame']['source'], 'device_cached')
        self.assertIsNone(av._state['image'])

    def test_explicit_host_frame_stays_offline(self):
        result = self.read({})
        self.assertEqual(result['pages'], ['fixture_frame_11'])
        self.assertEqual(result['frame']['source'], 'host_frame')
        self.assertEqual(result['frame']['path'], 'offline-old-frame.png')
        self.assertEqual(result['config_name'], 'offline-default')

    def test_failed_capture_invalidates_previous_frame(self):
        def failure():
            raise RuntimeError('fixture screenshot error')
        self.device.screenshot = failure
        with patch.object(av, '_device_engine', return_value=self.device):
            result = av.op_account_state(dict(capture=True))
        self.assertIn('fixture screenshot error', result['error'])
        self.assertIsNone(av._state['image'])
        self.assertIsNone(av._state['image_path'])

    def test_conflicting_sources_rejected_before_state_or_device_change(self):
        for args in (dict(device_cached=True, capture=True),
                     dict(device_cached=True, screenshot='any.png'),
                     dict(capture=True, screenshot='any.png'),
                     dict(device_cached='true')):
            with self.subTest(args=args), patch.object(av, '_device_engine') as factory:
                with self.assertRaises(ValueError):
                    av.op_account_state(args)
                factory.assert_not_called()
                self.assertIs(av._state['image'], self.old)


def core_checks():
    cases = []
    for name, options, cached in (
            ('dry_run', dict(dry_run=True), False),
            ('actions', dict(dry_run=False, allow_actions=True, serial='fixture-device'), True),
            ('read_only_device', dict(dry_run=False, read_only_device=True, serial='fixture-device'), True)):
        response = dict(frame=dict(available=True, source='device_cached' if cached else 'host_frame'),
                        pages=['fixture_current'], in_map=False, page_errors=[])
        cases.append(dict(name='boundary_cache_' + name, artifacts=True, **options,
                          tasks=[dict(id='state', kind='account_state', input={})],
                          stub_responses={'account_state': [
                              dict(expected_args=dict(capture=False, device_cached=cached), result=response),
                              dict(expected_args=dict(capture=False, device_cached=False), result=response)]},
                          expect=dict(outcome='succeeded', tasks=[dict(id='state', outcome='succeeded')])) )
    parent = ROOT / '.runtime/verification'
    with tempfile.TemporaryDirectory(prefix='boundary-cache-', dir=parent) as temporary:
        workspace = Path(temporary)
        fixture, output = workspace / 'fixture.json', workspace / 'result.json'
        fixture.write_text(json.dumps(dict(cases=cases)), encoding='utf-8')
        env = dict(os.environ, **dotnet_env.apply(os.environ, ROOT))
        proc = subprocess.run([str(ROOT / 'src/Alas.Server/bin/Release/net10.0/Alas.Server.exe'),
                               'selftest-runtime', '--fixture', str(fixture), '--json', str(output),
                               '--workspace', str(workspace / 'runs')], cwd=ROOT, env=env,
                              capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=60)
        assert proc.returncode == 0, proc.stdout + proc.stderr
        for case, expected in zip(json.loads(output.read_text(encoding='utf-8'))['cases'], cases):
            assert case['ok'], case
            artifact = json.loads(Path(case['tasks'][0]['artifact']).read_text(encoding='utf-8'))
            assert artifact['boundary_state']['pages'] == ['fixture_current']
            source = expected['stub_responses']['account_state'][0]['result']['frame']['source']
            assert artifact['boundary_state']['frame_source'] == source
    print('PASS: 3 Core boundary source cases; dry-run remains offline, native cache does not capture')


if __name__ == '__main__':
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(CachedAccountStateTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful():
        raise SystemExit(1)
    core_checks()
