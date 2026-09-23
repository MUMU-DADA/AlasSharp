#!/usr/bin/env python3
"""Exercise the native UI navigation host contract without a live device."""
import json
import sys
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import alas_vision as vision  # noqa: E402
from module.ui.page import Page  # noqa: E402
from module.ui.ui import UI  # noqa: E402


class Device:
    def __init__(self):
        self.config = object()


class FakeUI:
    instances = []
    failure = None
    switched = False

    def __init__(self, config, device):
        self.config = config
        self.device = device
        self.ui_current = None
        self.destination = None
        self.instances.append(self)

    def ui_ensure(self, destination, skip_first_screenshot=True):
        self.destination = destination
        self.skip_first_screenshot = skip_first_screenshot
        if self.failure:
            Page.init_connection(destination)
            raise self.failure
        self.ui_current = destination
        return self.switched

    def ui_page_appear(self, destination):
        return destination == self.ui_current


def main():
    device = Device()
    destination = Page.all_pages['page_campaign']
    FakeUI.instances = []
    with patch.object(vision, '_device_engine', return_value=device) as engine, \
            patch.object(sys.modules[UI.__module__], 'UI', FakeUI):
        denied = vision.op_ui_ensure({'destination': destination.name})
        assert denied['error_kind'] == 'ActionNotAllowed', denied
        unknown = vision.op_ui_ensure({'destination': 'page_does_not_exist',
                                       'allow_actions': True})
        assert unknown['error_kind'] == 'UnknownPage', unknown
        engine.assert_not_called()

        already = vision.op_ui_ensure({'destination': destination.name,
                                       'allow_actions': True})
        assert already['arrived'] and already['changed'] is False, already
        assert already['final_page'] == destination.name, already
        assert already['elapsed_ms'] >= 0, already
        assert FakeUI.instances[-1].config is device.config
        assert FakeUI.instances[-1].device is device
        assert FakeUI.instances[-1].destination is destination
        assert FakeUI.instances[-1].skip_first_screenshot is False
        engine.assert_called_once_with()

        FakeUI.switched = True
        switched = vision.op_ui_ensure({'destination': destination.name,
                                        'allow_actions': True})
        assert switched['arrived'] and switched['changed'] is True, switched

        FakeUI.failure = RuntimeError('fixture native failure')
        response = json.loads(vision.handle_line(json.dumps({
            'id': 1, 'op': 'ui_ensure',
            'args': {'destination': destination.name, 'allow_actions': True},
        })))
        assert response['ok'] is True, response
        failed = response['result']
        assert failed['arrived'] is False, failed
        assert failed['error_kind'] == 'RuntimeError', failed
        assert 'fixture native failure' in failed['error'], failed
        assert failed['traceback_tail'], failed
        assert all(page.parent is None for page in Page.all_pages.values())

    print('OK: native UI navigation host contract')


if __name__ == '__main__':
    main()
