#!/usr/bin/env python3
"""Raw and normal device captures must preserve the backend's channel order."""
from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np

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
    before = {key: av._state.get(key) for key in ('image', 'path')}
    checks = {}
    try:
        with patch.object(av, '_device_engine', return_value=device):
            for raw in (True, False):
                result = av.op_device_capture_set({'raw': raw})
                checks[f'raw={raw} preserves every pixel'] = (
                    result.get('raw') is raw
                    and np.array_equal(av._state['image'], expected))
        checks['both backend paths exercised'] = (
            device.raw_calls == device.normal_calls == 1)
    finally:
        av._state.update(before)
    for label, passed in checks.items():
        print(f'{"PASS" if passed else "FAIL"}: {label}')
    return 0 if all(checks.values()) else 1


if __name__ == '__main__':
    sys.exit(main())
