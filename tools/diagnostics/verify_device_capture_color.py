#!/usr/bin/env python3
"""Raw and normal device captures must preserve the backend's channel order."""
from __future__ import annotations

import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import alas_vision as av  # noqa: E402


class Device:
    def __init__(self, image):
        self.config = SimpleNamespace(Emulator_ScreenshotMethod='scrcpy')
        self.image = image
        self.raw_calls = 0
        self.normal_calls = 0

    def screenshot_scrcpy(self):
        self.raw_calls += 1
        return self.image.copy()

    def screenshot(self):
        self.normal_calls += 1
        return self.image.copy()


def main() -> int:
    expected = np.zeros((16, 16, 3), dtype=np.uint8)
    expected[:, :] = (13, 79, 241)
    expected[4:12, 4:12] = (221, 37, 91)
    device = Device(expected)
    before = {key: av._state.get(key) for key in ('image', 'image_path')}
    checks = {}
    try:
        av._state['image_path'] = 'stored-frame.png'
        with tempfile.TemporaryDirectory() as temporary:
            with patch.object(av, '_device_engine', return_value=device), \
                    patch.object(tempfile, 'tempdir', temporary):
                for raw in (True, False):
                    result = av.op_device_capture_set({'raw': raw})
                    checks[f'raw={raw} preserves every pixel'] = (
                        result.get('raw') is raw
                        and np.array_equal(av._state['image'], expected))
                    checks[f'raw={raw} clears stored frame provenance'] = (
                        av._state['image_path'] is None)
                checks['capture leaves no screenshot in system temp'] = not list(Path(temporary).iterdir())
        checks['both backend paths exercised'] = (
            device.raw_calls == device.normal_calls == 1)

        rgba = Image.fromarray(np.dstack((expected, np.full(expected.shape[:2], 127, dtype=np.uint8))))
        with patch.object(av, '_device_engine', return_value=Device(rgba)):
            result = av.op_device_capture_set({'raw': True})
            checks['PIL alpha is dropped like upstream load_image'] = (
                'error' not in result and np.array_equal(av._state['image'], expected))

        failed = Device(expected)
        raised = False
        with patch.object(av, '_device_engine', return_value=failed), \
                patch.object(failed, 'screenshot_scrcpy', side_effect=RuntimeError('capture failed')):
            try:
                av.op_device_capture_set({'raw': True})
            except RuntimeError:
                raised = True
        checks['failed capture cannot expose previous frame'] = (
            raised and av._state['image'] is None and av._state['image_path'] is None)
    finally:
        av._state.update(before)
    for label, passed in checks.items():
        print(f'{"PASS" if passed else "FAIL"}: {label}')
    return 0 if all(checks.values()) else 1


if __name__ == '__main__':
    sys.exit(main())
