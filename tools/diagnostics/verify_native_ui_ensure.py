#!/usr/bin/env python3
"""Exercise the native UI navigation host contract without a live device."""
import json
import sys
import tempfile
from pathlib import Path
from unittest.mock import Mock, patch

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import alas_vision as vision  # noqa: E402
from module.ui.page import Page  # noqa: E402
from module.ui.ui import UI  # noqa: E402


class Device:
    def __init__(self):
        self.config = object()
        self.image = np.full((8, 12, 3), (12, 34, 56), dtype=np.uint8)
        self.has_cached_image = True
        self.visible = True
        self.captures = 0
        self.clicks = []

    def click(self, button):
        self.clicks.append(button)

    def screenshot(self):
        self.captures += 1
        return self.image


class FakeUI:
    instances = []
    failure = None
    switched = False
    click_on_ensure = False

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
        if self.click_on_ensure:
            self.device.click('native-edge')
        self.ui_current = destination
        return self.switched

    def ui_page_appear(self, destination):
        return destination == self.ui_current and self.device.visible


def verify_idle_handler():
    module = sys.modules[UI.__module__]
    for active in (None, 'IDLE', 'IDLE_2', 'IDLE_3'):
        buttons = {name: Mock(name=name) for name in ('IDLE', 'IDLE_2', 'IDLE_3')}
        for name, button in buttons.items():
            button.match_luma.return_value = name == active
        reward = Mock(name='REWARD_GOTO_MAIN')
        timer = Mock()
        timer.reached.return_value = True
        handler = Mock()
        handler.get_interval_timer.return_value = timer
        handler.device.image = object()
        with patch.multiple(module, **buttons, REWARD_GOTO_MAIN=reward):
            assert UI.handle_idle_page(handler) is (active is not None)
        handler.get_interval_timer.assert_called_once_with(buttons['IDLE'], interval=3)
        if active is None:
            handler.device.click.assert_not_called()
            timer.reset.assert_not_called()
        else:
            handler.device.click.assert_called_once_with(reward)
            timer.reset.assert_called_once_with()

    timer.reset_mock()
    timer.reached.return_value = False
    handler.device.click.reset_mock()
    with patch.multiple(module, **buttons, REWARD_GOTO_MAIN=reward):
        assert UI.handle_idle_page(handler) is False
    assert all(button.match_luma.call_count == 1 for button in buttons.values())
    handler.device.click.assert_not_called()


def main():
    verify_idle_handler()
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
        assert device.captures == 1, device.captures

        FakeUI.switched = True
        FakeUI.click_on_ensure = True
        with patch.object(vision.time, 'sleep') as sleep:
            switched = vision.op_ui_ensure({'destination': destination.name,
                                            'allow_actions': True})
        sleep.assert_called_once_with(1.0)
        assert switched['arrived'] and switched['changed'] is True, switched
        assert device.clicks == ['native-edge'] and 'click' not in vars(device)
        assert device.captures == 2, device.captures
        FakeUI.click_on_ensure = False
        device.visible = False
        unstable = vision.op_ui_ensure({'destination': destination.name,
                                        'allow_actions': True})
        assert not unstable['arrived'] and unstable['error_kind'] == 'DestinationNotVisible', unstable
        assert device.captures == 3, device.captures
        device.visible = True

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
        assert all(line in failed['error'] for line in failed['traceback_tail']), failed
        assert all(page.parent is None for page in Page.all_pages.values())

        with tempfile.TemporaryDirectory(prefix='alas-nav-frame-') as directory:
            frame = Path(directory) / 'failure.png'
            arguments = {'destination': destination.name, 'allow_actions': True,
                         'failure_frame': str(frame)}
            failed = vision.op_ui_ensure(arguments)
            assert failed['failure_frames'] == [str(frame)], failed
            assert failed['failure_frame_source'] == 'last_cached_device_frame', failed
            assert np.array_equal(np.asarray(Image.open(frame)), device.image)
            before = frame.read_bytes()
            # Existing artifacts must not be overwritten; save failure must retain the cause.
            collision = vision.op_ui_ensure(arguments)
            assert frame.read_bytes() == before
            assert collision['failure_frames'] == []
            assert 'fixture native failure' in collision['error']
            assert 'FileExistsError' in collision['failure_frame_error']
            assert collision['failure_frame_error'] in collision['error']

            device.has_cached_image = False
            missing = vision.op_ui_ensure({**arguments, 'failure_frame': str(Path(directory) / 'missing.png')})
            assert missing['failure_frames'] == [] and missing['failure_frame_source'] == 'unavailable'
            device.has_cached_image = True
            FakeUI.failure = SystemExit(1)
            exited = vision.op_ui_ensure({**arguments, 'failure_frame': str(Path(directory) / 'exit.png')})
            assert exited['error_kind'] == 'SystemExit' and not exited['arrived']
            assert all(line in exited['error'] for line in exited['traceback_tail'])
            assert len(exited['failure_frames']) == 1
            FakeUI.failure = None
            success = vision.op_ui_ensure({**arguments, 'failure_frame': str(Path(directory) / 'success.png')})
            assert success['arrived'] and success.get('failure_frames', []) == []
            assert not (Path(directory) / 'success.png').exists()
            engine.reset_mock()
            bad_path = vision.op_ui_ensure({**arguments, 'failure_frame': '../relative.png'})
            assert bad_path['error_kind'] == 'InvalidArtifactPath'
            engine.assert_not_called()

    print('OK: native UI navigation host contract')


if __name__ == '__main__':
    main()
