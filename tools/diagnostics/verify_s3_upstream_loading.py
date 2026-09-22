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
from contextlib import redirect_stdout
import io
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
