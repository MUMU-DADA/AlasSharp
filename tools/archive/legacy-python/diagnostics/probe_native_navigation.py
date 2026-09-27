"""Compare native navigation's cached result with fresh backend and ADB frames.

Frames and results stay in the ignored .runtime directory. The only game action
is the explicitly requested upstream UI.ui_ensure call.
"""
import argparse
import json
import sys
import time
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))

import alas_vision as vision  # noqa: E402
import adb_util  # noqa: E402


def call(name, **args):
    response = json.loads(vision.handle_line(json.dumps(
        {'id': 1, 'op': name, 'args': args})))
    if not response.get('ok'):
        raise RuntimeError(response.get('error'))
    return response['result']


def capture(path, source, serial):
    if source == 'backend':
        call('device_screencap', path=str(path))
    else:
        adb_util.screencap(str(path), serial)
    call('screenshot_load', path=str(path))
    return call('page_current')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', help='upstream page_* name')
    parser.add_argument('--run', action='store_true', help='allow one upstream navigation action')
    parser.add_argument('--serial', default=adb_util.SERIAL)
    parser.add_argument('--settle-ms', type=int, default=0,
                        help='diagnostic only: pause after each native click')
    args = parser.parse_args()
    if not args.run:
        parser.error('--run is required for navigation')
    if not adb_util.ensure(args.serial):
        raise RuntimeError('ADB device is not ready')

    output = TOOLS.parent / '.runtime' / 'diagnostics' / ('native-navigation-' +
        time.strftime('%Y%m%dT%H%M%S'))
    output.mkdir(parents=True, exist_ok=False)
    call('device_configure', serial=args.serial, screenshot='scrcpy', control='MaaTouch')
    device = vision._device_engine() if args.settle_ms else None
    previous_click = vars(device).get('click') if device is not None else None
    had_click_override = device is not None and 'click' in vars(device)
    if device is not None:
        native_click = device.click

        def settled_click(*click_args, **click_kwargs):
            value = native_click(*click_args, **click_kwargs)
            time.sleep(args.settle_ms / 1000)
            return value

        device.click = settled_click
    result = {'destination': args.destination, 'settle_ms': args.settle_ms, 'frames': []}
    try:
        result['native'] = call('ui_ensure', destination=args.destination,
                                allow_actions=True, failure_frame=str(output / 'failure.png'))
    finally:
        if device is not None:
            if had_click_override:
                device.click = previous_click
            else:
                del device.click
    for delay in (0, 0.5, 2):
        if delay:
            time.sleep(delay)
        for source in ('backend', 'adb'):
            path = output / ('%s-%s.png' % (source, len(result['frames'])))
            result['frames'].append({'source': source, 'delay_s': delay,
                                     'path': str(path), **capture(path, source, args.serial)})
    with (output / 'result.json').open('w', encoding='utf-8') as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
    print(json.dumps({'native': result['native'],
                      'frames': [{'source': frame['source'], 'delay_s': frame['delay_s'],
                                  'hit': frame['hit'], 'errors': frame['errors']}
                                 for frame in result['frames']],
                      'output': str(output)}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
