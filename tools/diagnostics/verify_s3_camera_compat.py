#!/usr/bin/env python3
"""Exercise the real upstream swipe wait loop with deterministic offline frames."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))

import alas_vision  # Establish the upstream import path; does not touch a device.
import numpy as np
import module.map.camera as camera_module
from s3_camera_compat import apply_camera_previous_view_compat, guard_previous_view_comparison


def upstream_with_none_guard(self):
    prev_center_offset = None

    def is_still_prev():
        if prev_center_offset is None:
            return False
        return np.linalg.norm(self.view.center_offset - prev_center_offset) < 0.001

    return is_still_prev()


class CameraHarness:
    def __init__(self, offsets, previous=None, timeout_frames=20):
        self.offsets = list(offsets)
        self.frames = 0
        self.timeout_frames = timeout_frames
        self.final_updates = 0
        self.interval_clears = 0
        self.view = SimpleNamespace(center_offset=np.array([0.0, 0.0]))
        self._prev_view = (None if previous is None else
                           SimpleNamespace(center_offset=np.array(previous)))
        self.config = SimpleNamespace(MAP_GRID_CENTER_TOLERANCE=0.1)
        self.device = SimpleNamespace(
            screenshot=self.screenshot,
            _screenshot_interval=SimpleNamespace(clear=self.clear_interval),
        )

    def clear_interval(self):
        self.interval_clears += 1

    def screenshot(self):
        self.frames += 1
        if self.frames > len(self.offsets):
            raise AssertionError('upstream swipe wait consumed unexpected extra frames')

    def _update_view(self):
        self.view.center_offset = np.array(self.offsets[self.frames - 1])
        return True

    def _update_view_data(self):
        self.final_updates += 1

    def timer(self, seconds, **_kwargs):
        owner = self

        class Timer:
            def start(self):
                return self

            def reset(self):
                return self

            def reached(self):
                return seconds == 0.35 and owner.frames >= owner.timeout_frames

        return Timer()


class CameraCompatibilityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        original = camera_module.Camera.update
        cls.original = staticmethod(original)
        cls.guarded = staticmethod(guard_previous_view_comparison(original))

    def run_camera(self, harness, original=False, wait_swipe=True):
        function = self.original if original else self.guarded
        with patch.object(camera_module, 'Timer', side_effect=harness.timer):
            function(harness, wait_swipe=wait_swipe)

    def test_original_reproduces_missing_previous_offset_error(self):
        camera = CameraHarness([(0.4, 0.1)])
        with self.assertRaisesRegex(TypeError, 'NoneType'):
            self.run_camera(camera, original=True)
        self.assertEqual(camera.final_updates, 0)

    def test_no_previous_view_still_waits_for_grid_center(self):
        camera = CameraHarness([(0.4, 0.1), (0.5, 0.5)])
        self.run_camera(camera)
        self.assertEqual(camera.frames, 2)
        self.assertEqual(camera.final_updates, 1)
        self.assertIsNone(camera._prev_view)

    def test_existing_previous_view_preserves_swipe_departure_and_return(self):
        offsets = [(0.5, 0.5), (0.4, 0.1), (0.48, 0.52)]
        actual = CameraHarness(offsets, previous=(0.5, 0.5))
        expected = CameraHarness(offsets, previous=(0.5, 0.5))
        self.run_camera(actual)
        self.run_camera(expected, original=True)
        self.assertEqual(actual.frames, 3)
        self.assertEqual((actual.frames, actual.final_updates, actual.interval_clears),
                         (expected.frames, expected.final_updates, expected.interval_clears))

    def test_swipe_timeout_keeps_upstream_exit(self):
        camera = CameraHarness([(0.4, 0.1)] * 3, timeout_frames=3)
        self.run_camera(camera)
        self.assertEqual(camera.frames, 3)
        self.assertEqual(camera.final_updates, 1)

    def test_no_wait_path_is_unchanged(self):
        camera = CameraHarness([(0.4, 0.1)])
        self.run_camera(camera, wait_swipe=False)
        self.assertEqual(camera.frames, 1)
        self.assertEqual(camera.final_updates, 1)

    def test_installation_is_idempotent(self):
        with patch.object(camera_module.Camera, 'update', self.original):
            self.assertTrue(apply_camera_previous_view_compat())
            installed = camera_module.Camera.update
            self.assertFalse(apply_camera_previous_view_compat())
            self.assertIs(camera_module.Camera.update, installed)
            self.assertIs(installed.__wrapped__, self.original)

    def test_upstream_none_guard_needs_no_patch(self):
        self.assertIs(guard_previous_view_comparison(upstream_with_none_guard),
                      upstream_with_none_guard)


if __name__ == '__main__':
    unittest.main(verbosity=2)
