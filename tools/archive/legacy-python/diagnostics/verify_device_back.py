#!/usr/bin/env python3
"""Check the navigation return key against the upstream Device interface."""
import json
import sys
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import alas_vision as vision  # noqa: E402


class Device:
    def __init__(self, failure=None):
        self.calls = []
        self.failure = failure

    def adb_shell(self, command):
        self.calls.append(command)
        if self.failure:
            raise self.failure


def call(device):
    with patch.object(vision, '_device_engine', return_value=device):
        response = json.loads(vision.handle_line(json.dumps({
            'id': 1, 'op': 'device_back', 'args': {},
        })))
    assert response['ok'] is True, response
    return response['result']


def main():
    device = Device()
    assert call(device) == {'ok': True}
    assert device.calls == [['input', 'keyevent', '4']], device.calls

    failed = Device(RuntimeError('fixture keyevent failed'))
    result = call(failed)
    assert result['ok'] is False and result['error'] == 'RuntimeError: fixture keyevent failed', result
    assert failed.calls == [['input', 'keyevent', '4']], failed.calls
    print('OK: upstream return key and failure propagation')


if __name__ == '__main__':
    main()
