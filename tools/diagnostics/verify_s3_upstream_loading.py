#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Offline regression for the upstream campaign loader and inherited map config.

Uses the production S3 initializer with a screenshot-only fake device. All configs
are upstream read-only template configs; no emulator or account file is touched.
The oracle is CampaignRun.load_campaign(), the upstream application's entry point.

Run with .runtime/venv314/Scripts/python.exe tools/diagnostics/verify_s3_upstream_loading.py
"""
from __future__ import annotations

import importlib
import copy
from contextlib import ExitStack, redirect_stdout
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))

import alas_vision as av
import numpy as np
from module.base.base import ModuleBase
from module.base.utils import load_image
from module.campaign.run import CampaignRun
from module.config.config import AzurLaneConfig
from module.config.config_updater import ConfigUpdater
from module.config.utils import parse_value
from module.exception import MapDetectionError
from module.map_detection.view import View


def template_config(*_args, **_kwargs):
    return AzurLaneConfig('template')


def config_values(config, module):
    """Compare every map-config attribute consumed by the upstream merge."""
    return {name: getattr(config, name) for name in dir(module.Config())
            if not name.startswith('_') and hasattr(config, name)
            and not callable(getattr(module.Config(), name))}


class UpstreamLoadingTests(unittest.TestCase):
    def setUp(self):
        self.saved_campaign = dict(av._CAMPAIGN)
        self.device_calls = []
        self.device = SimpleNamespace(
            image=np.zeros((720, 1280, 3), dtype=np.uint8),
            screenshot=lambda: self.device_calls.append('screenshot'),
            stuck_record_clear=lambda: self.device_calls.append('stuck_record_clear'),
            click_record_clear=lambda: self.device_calls.append('click_record_clear'),
        )
        self.early_ocr = patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True)
        self.early_ocr.start()

    def tearDown(self):
        av._CAMPAIGN.clear()
        av._CAMPAIGN.update(self.saved_campaign)
        self.early_ocr.stop()

    def initialize(self, chapter):
        with patch.object(av, '_device_engine', return_value=self.device), \
                patch.object(av, '_map_config', side_effect=template_config):
            result = av.op_s3_campaign_init({'chapter': chapter})
        self.assertNotIn('error', result, result)
        self.assertTrue(result.get('instantiated'), result)
        self.assertEqual(self.device_calls[-3:],
                         ['stuck_record_clear', 'click_record_clear', 'screenshot'])
        return av._CAMPAIGN['obj']

    def test_screenshot_failure_does_not_publish_initialized_campaign(self):
        av._CAMPAIGN.clear()

        def failed_screenshot():
            self.device_calls.append('screenshot')
            raise RuntimeError('offline screenshot failure')

        self.device.screenshot = failed_screenshot
        with patch.object(av, '_device_engine', return_value=self.device), \
                patch.object(av, '_map_config', side_effect=template_config):
            result = av.op_s3_campaign_init({
                'chapter': 'campaign.campaign_main.campaign_1_4',
            })
        self.assertIn('offline screenshot failure', result.get('error', ''))
        self.assertFalse(result.get('instantiated'))
        self.assertEqual(av.op_s3_campaign_info({}), {'initialized': False})
        self.assertNotIn('loader', av._CAMPAIGN)
        self.assertEqual(self.device_calls,
                         ['stuck_record_clear', 'click_record_clear', 'screenshot'])

    def test_failed_reinitialization_cannot_execute_previous_campaign(self):
        # A successful first chapter used to survive every later init failure,
        # leaving the low-level action endpoint able to execute the wrong map.
        for fault in ('native_import', 'device', 'config', 'bind', 'loader', 'screenshot'):
            with self.subTest(fault=fault):
                prior = self.initialize('campaign.campaign_main.campaign_1_4')
                requested = 'campaign.campaign_main.campaign_2_2'
                config = template_config()
                with ExitStack() as patches:
                    device = patches.enter_context(patch.object(av, '_device_engine', return_value=self.device))
                    settings = patches.enter_context(patch.object(av, '_map_config', return_value=config))
                    old_action = patches.enter_context(patch.object(prior, 'execute_a_battle'))
                    if fault == 'native_import':
                        requested = 'campaign.campaign_main.missing'
                    elif fault == 'device':
                        device.side_effect = RuntimeError('fixture device failure')
                    elif fault == 'config':
                        settings.side_effect = RuntimeError('fixture config failure')
                    elif fault == 'bind':
                        patches.enter_context(patch.object(config, 'bind', side_effect=RuntimeError('fixture bind failure')))
                    elif fault == 'loader':
                        patches.enter_context(patch.object(CampaignRun, 'load_campaign', side_effect=RuntimeError('fixture loader failure')))
                    else:
                        patches.enter_context(patch.object(self.device, 'screenshot', side_effect=RuntimeError('fixture screenshot failure')))
                    try:
                        failed = av.op_s3_campaign_init({'chapter': requested})
                    except RuntimeError as error:
                        self.assertIn(fault, str(error))
                    else:
                        self.assertTrue(failed.get('error'), failed)
                    self.assertEqual(av.op_s3_campaign_info({}), {'initialized': False})
                    self.assertEqual(av._CAMPAIGN, {})
                    calls_before = list(self.device_calls)
                    refused = av.op_s3_campaign_call({'name': 'execute_a_battle', 'allow_actions': True})
                    self.assertIn('尚未初始化', refused['error'])
                    self.assertIn('尚未初始化', av.op_s3_probe_view({})['error'])
                    old_action.assert_not_called()
                    self.assertEqual(self.device_calls, calls_before)
                # The shared device remains usable after a failed chapter attempt.
                current = self.initialize('campaign.campaign_main.campaign_2_2')
                self.assertIsNot(current, prior)
                self.assertEqual(av._CAMPAIGN['chapter'], 'campaign.campaign_main.campaign_2_2')

    def test_invalid_mode_invalidates_previous_campaign_without_device_access(self):
        prior = self.initialize('campaign.campaign_main.campaign_1_4')
        with patch.object(av, '_device_engine') as device, patch.object(av, '_map_config') as config, \
                patch.object(prior, 'execute_a_battle') as old_action:
            with self.assertRaisesRegex(ValueError, 'mode'):
                av.op_s3_campaign_init({'mode': 'invalid'})
            self.assertEqual(av.op_s3_campaign_info({}), {'initialized': False})
            self.assertIn('尚未初始化', av.op_s3_campaign_call(
                {'name': 'execute_a_battle', 'allow_actions': True})['error'])
            device.assert_not_called()
            config.assert_not_called()
            old_action.assert_not_called()

    def test_invalid_native_dependencies_fail_before_device_construction(self):
        for chapter in ('invalid.module', 'campaign.campaign_main.missing',
                        'campaign.event_20200227_cn.c2', 'campaign.event_20200312_cn.sp3'):
            with self.subTest(chapter=chapter), \
                    patch.object(av, '_device_engine') as device:
                result = av.op_s3_campaign_init({'chapter': chapter})
                self.assertFalse(result['instantiated'])
                self.assertTrue(result['error'])
                self.assertTrue(result['traceback_tail'])
                device.assert_not_called()

    def test_production_initializer_matches_upstream_loader(self):
        # Includes a Config imported from another chapter (1-4), changed geometry
        # (7-1), and later chapters with different map capabilities.
        for name in ('campaign_1_4', 'campaign_7_1', 'campaign_2_2',
                     'campaign_11_1', 'campaign_14_1'):
            chapter = 'campaign.campaign_main.' + name
            with self.subTest(chapter=chapter):
                module = importlib.import_module(chapter)
                expected_loader = CampaignRun(template_config(), self.device)
                expected_loader.load_campaign(name, folder='campaign_main')
                actual = self.initialize(chapter)
                self.assertIsInstance(av._CAMPAIGN.get('loader'), CampaignRun)
                self.assertEqual(config_values(actual.config, module),
                                 config_values(expected_loader.campaign.config, module))

    def test_session_instance_and_transport_match_fresh_native_config(self):
        """Real Config/loader and host binding; config storage and device are inert."""
        catalog = json.loads((Path(av.FORK) / 'module/config/argument/args.json').read_text(encoding='utf-8'))
        defaults = {task: {group: {key: parse_value(arg['value'], arg) for key, arg in fields.items()}
                                  for group, fields in groups.items()} for task, groups in catalog.items()}
        chapter = 'campaign.campaign_main.campaign_1_4'
        for owner, backend, changed in ((None, 'scrcpy', False), ('fixture-alpha', 'scrcpy', False),
                                        ('fixture-alpha', 'auto', False), ('fixture-alpha', 'scrcpy', True)):
            with self.subTest(owner=owner, backend=backend, changed=changed):
                store = {name: copy.deepcopy(defaults) for name in ('alas', 'fixture-alpha')}
                for name, data in store.items():
                    data['Alas']['Emulator'].update(Serial='fixture-device', ScreenshotMethod='adb',
                                                   ControlMethod='ADB', PackageName='fixture.' + name)
                    data['General']['Fixture'] = {'Value': name}
                reads, devices, shots = [], [], []

                def read(_config, name, is_template=False):
                    reads.append(name)
                    return copy.deepcopy(store[name])

                def write(name, data, mod_name='alas'):
                    self.assertEqual(mod_name, 'alas')
                    store[name] = copy.deepcopy(data)

                def construct(config):
                    device = SimpleNamespace(config=config, image=np.zeros((8, 12, 3), dtype=np.uint8),
                        screenshot=lambda: shots.append(True), stuck_record_clear=lambda: None,
                        click_record_clear=lambda: None)
                    devices.append(device)
                    return device

                with patch.object(ConfigUpdater, 'read_file', read), \
                        patch.object(ConfigUpdater, 'write_file', staticmethod(write)), \
                        patch('module.device.device.Device', side_effect=construct), \
                        patch.object(av, '_DEVICE_OBJ', None), patch.object(av, '_DEVICE_KEY', None), \
                        patch.object(av, '_DEVICE_ARGS', dict(serial='fixture-device', screenshot=backend, control='MaaTouch')):
                    previous = None
                    if owner:
                        previous = AzurLaneConfig(owner, task='Reward')
                        previous.override(Fixture_Value='previous-task-only')
                        av._device_engine(config=previous)
                    if changed:
                        store[owner]['Alas']['Emulator']['PackageName'] = 'fixture.changed'
                    reads.clear()
                    emulator_before = copy.deepcopy(store[owner or 'alas']['Alas']['Emulator'])
                    try:
                        result = av.op_s3_campaign_init({'chapter': chapter})
                    except RuntimeError as error:
                        self.assertTrue(changed, str(error))
                        self.assertIn('实例或设备配置已变化', str(error))
                        result = {'error': str(error)}
                    if changed:
                        self.assertTrue(result.get('error'), result)
                        self.assertEqual(av._CAMPAIGN, {})
                        self.assertEqual(shots, [])
                    else:
                        self.assertTrue(result.get('instantiated'), result)
                        actual, loader = av._CAMPAIGN['obj'], av._CAMPAIGN['loader']
                        self.assertEqual(actual.config.config_name, owner or 'alas')
                        self.assertEqual(loader.config.config_name, devices[0].config.config_name)
                        self.assertEqual(actual.config.Fixture_Value, owner or 'alas')
                        self.assertIsNot(loader.config, previous)
                        self.assertIs(actual.device, devices[0])
                        self.assertEqual(actual.config.Emulator_ScreenshotMethod,
                                         'adb' if backend == 'auto' else backend)
                        self.assertIs(actual.MAP, importlib.import_module(chapter).MAP)
                        self.assertEqual(shots, [True])
                    self.assertEqual(set(reads), {owner or 'alas'})
                    self.assertEqual(len(devices), 1)
                    self.assertEqual(store[owner or 'alas']['Alas']['Emulator'], emulator_before)
                    if previous:
                        self.assertIs(devices[0].config, previous)
                        self.assertEqual(previous.Fixture_Value, 'previous-task-only')

    def test_explicit_mode_uses_native_override_before_chapter_merge(self):
        chapter = 'campaign.campaign_main.campaign_1_1'
        for requested in ('hard', 'normal', None):
            with self.subTest(mode=requested):
                config = template_config()
                config.override(Campaign_Mode='hard')
                persisted = copy.deepcopy(config.data)
                expected = copy.deepcopy(config)
                if requested is not None:
                    expected.override(Campaign_Mode=requested)
                expected_loader = CampaignRun(expected, self.device)
                expected_loader.load_campaign('campaign_1_1')
                with patch.object(av, '_device_engine', return_value=self.device), \
                        patch.object(av, '_map_config', return_value=config):
                    result = av.op_s3_campaign_init({'chapter': chapter, 'mode': requested})
                self.assertNotIn('error', result, result)
                actual = av._CAMPAIGN['obj']
                self.assertEqual(actual.config.Campaign_Mode,
                                 expected_loader.campaign.config.Campaign_Mode)
                self.assertEqual(result['campaign_mode'], actual.config.Campaign_Mode)
                self.assertEqual(config.data, persisted)
                self.assertIs(actual.MAP, expected_loader.campaign.MAP)
                self.assertEqual(config_values(actual.config, importlib.import_module(chapter)),
                                 config_values(expected_loader.campaign.config, importlib.import_module(chapter)))

    def test_invalid_mode_rejected_before_config_or_device(self):
        for mode in ('', 'Hard', 'auto', False, 1, [], {}):
            with self.subTest(mode=mode), patch.object(av, '_device_engine') as device, \
                    patch.object(av, '_map_config') as config:
                with self.assertRaisesRegex(ValueError, 'mode'):
                    av.op_s3_campaign_init({'chapter': 'campaign.campaign_main.campaign_1_1', 'mode': mode})
                device.assert_not_called()
                config.assert_not_called()

    def test_diagnostic_config_matches_upstream_loader(self):
        # Keep the actual helper and its chapter merge; replace only config-file
        # construction, so this also catches diagnostics silently using defaults.
        original_init = AzurLaneConfig.__init__

        def read_only_init(config, _name, task=None):
            original_init(config, 'template', task=task)

        with patch.object(AzurLaneConfig, '__init__', new=read_only_init):
            first = av._map_config('campaign.campaign_main.campaign_1_4')
            second = av._map_config('campaign.campaign_main.campaign_7_1')
            plain = av._map_config()
        for name, actual in (('campaign_1_4', first), ('campaign_7_1', second)):
            module = importlib.import_module('campaign.campaign_main.' + name)
            loader = CampaignRun(template_config(), self.device)
            loader.load_campaign(name, folder='campaign_main')
            self.assertEqual(config_values(actual, module),
                             config_values(loader.campaign.config, module))
        self.assertEqual(plain.INTERNAL_LINES_HOUGHLINES_THRESHOLD,
                         template_config().INTERNAL_LINES_HOUGHLINES_THRESHOLD)
        self.assertIsNot(first, second)

    def test_exported_config_fields_and_types_match_native_classes(self):
        from s3_preflight import compare_exported_config
        # Import, inherited Config, nested arithmetic and Campaign-only data.
        chapters = ('campaign_main.campaign_1_4', 'campaign_main.campaign_14_1',
                    'event_20260908_cn.a1', 'war_archives_20230525_cn.t1')
        with patch.object(av, '_device_engine', side_effect=AssertionError('audit touched device')), \
                patch.object(av, 'op_s3_campaign_init', side_effect=AssertionError('audit initialized Campaign')), \
                patch.object(AzurLaneConfig, '__init__', side_effect=AssertionError('audit loaded account config')):
            for chapter in chapters:
                with self.subTest(chapter=chapter):
                    path = ROOT / 'data' / 'campaign' / Path(*chapter.split('.')).with_suffix('.json')
                    ir = json.loads(path.read_text(encoding='utf-8'))
                    module = importlib.import_module('campaign.' + chapter)
                    self.assertEqual(compare_exported_config(ir, module.Config), [])

    def test_native_config_audit_detects_missing_fields_and_lost_tuple_type(self):
        from s3_preflight import compare_exported_config
        chapter = 'campaign.campaign_main.campaign_1_4'
        module = importlib.import_module(chapter)
        ir = json.loads((ROOT / 'data/campaign/campaign_main/campaign_1_4.json')
                        .read_text(encoding='utf-8'))
        broken = copy.deepcopy(ir)
        del broken['config']['INTERNAL_LINES_FIND_PEAKS_PARAMETERS']
        differences = compare_exported_config(broken, module.Config)
        self.assertIn('缺少字段 INTERNAL_LINES_FIND_PEAKS_PARAMETERS', differences)
        broken = copy.deepcopy(ir)
        broken['config_meta']['typed_values']['HOMO_CANNY_THRESHOLD']['type'] = 'list'
        differences = compare_exported_config(broken, module.Config)
        self.assertIn('字段值或类型不一致 HOMO_CANNY_THRESHOLD', differences)

    def test_known_camera_frames_use_inherited_config(self):
        cases = [
            # This previously failed at perspective initialization because the
            # adapter omitted the inherited Config, including dark-line peaks.
            ('campaign_1_4', '_map_init_fail_campaign_1_4_att2.png', (6, 2), 21, 2, 1, False),
            ('campaign_1_4', '_14_entrypos.png', (6, 2), 21, 2, 1, False),
            # A one-row map can have the screen center outside its only row.
            # The upstream Camera._update_view handles that by moving the camera.
            # It still must have found all seven cells and the actual occupants.
            ('campaign_1_1', 'fixtures/subchapter_1_1.png', (6, 0), 7, 1, 1, True),
            ('campaign_2_2', 'fixtures/inmap_2-2.png', (6, 4), 35, 2, 1, False),
        ]
        for name, frame, shape, cells, enemies, fleets, outside in cases:
            with self.subTest(frame=frame):
                path = ROOT / 'data' / frame
                self.assertTrue(path.is_file(), f'Missing captured fixture: {path}')
                campaign = self.initialize('campaign.campaign_main.' + name)
                view = View(campaign.config)
                try:
                    view.load(load_image(str(path)))
                except MapDetectionError as error:
                    self.assertTrue(outside, str(error))
                    self.assertEqual(str(error), 'Camera outside map: offset=(0, 1)')
                view.predict()
                self.assertEqual(tuple(int(value) for value in view.shape), shape)
                self.assertEqual(len(view.grids), cells)
                self.assertEqual(len(view.select(is_enemy=True)), enemies)
                self.assertEqual(len(view.select(is_fleet=True)), fleets)

    def test_preflight_is_offline_and_missing_fixture_is_unverified(self):
        import s3_preflight
        output = io.StringIO()
        with patch.object(av, '_device_engine', side_effect=AssertionError('preflight touched device')), \
                patch.object(av, 'op_s3_campaign_init', side_effect=AssertionError('preflight initialized campaign')), \
                redirect_stdout(output):
            result = s3_preflight.main([
                'campaign.campaign_main.campaign_1_3',
                '--fixture', 'data/fixtures/fixture_does_not_exist.png',
            ])
        self.assertEqual(result, 0, output.getvalue())
        self.assertIn('[UNTESTED]', output.getvalue())
        self.assertNotIn('[FAIL]', output.getvalue())


if __name__ == '__main__':
    unittest.main(verbosity=2)
