"""Replay actual Device stuck/control checks with a fake clock and no device connection."""
import contextlib
from collections import deque
import hashlib
import json
import os
from pathlib import Path
import sys


def main():
    root, inputs, output = (Path(p).resolve() for p in sys.argv[1:])
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import module.base.timer as timers
        import module.device.device as module
        from module.logger import logger
        logger.setLevel('CRITICAL')
        module.show_function_call = lambda: None

        class Replay(module.Device):
            def __init__(self, running):
                self.now = 100.
                timers.time = lambda: self.now
                self.detect_record = set()
                self.click_record = deque(maxlen=15)
                self.stuck_timer = timers.Timer(60, count=60).start()
                self.stuck_timer_long = timers.Timer(180, count=180).start()
                self.running, self.inspections = running, 0
            def app_is_running(self):
                self.inspections += 1
                return self.running

        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(sample['running'])
            errors, removals, successful = [], [], 0
            for index, op in enumerate(sample['operations']):
                try:
                    if op['kind'] == 'time': replay.now += op['seconds']
                    elif op['kind'] == 'observe': replay.stuck_record_add(op['name'])
                    elif op['kind'] == 'capture': replay.stuck_record_check()
                    elif op['kind'] == 'control':
                        replay.handle_control_check(op['name'])
                        successful += 1
                    elif op['kind'] == 'reset':
                        replay.stuck_record_clear()
                        replay.click_record_clear()
                    elif op['kind'] == 'wait_reset': replay.stuck_record_clear()
                    elif op['kind'] == 'controls_reset': replay.click_record_clear()
                    elif op['kind'] == 'remove': removals.append(replay.click_record_remove(op['name']))
                    else: raise AssertionError(op)
                except (module.GameStuckError, module.GameNotRunningError, module.GameTooManyClickError) as error:
                    errors.append(dict(index=index, kind=type(error).__name__))
            results.append(dict(errors=errors, removals=removals, successful=successful, inspections=replay.inspections))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
                   ['module/device/device.py', 'module/base/base.py', 'module/device/control.py', 'module/base/timer.py']}
        import cv2
        import numpy as np
        from module.handler.assets import IN_MAP
        from module.base.utils import load_image
        image = np.zeros((720, 1280, 3), dtype=np.uint8)
        x, y, right, bottom = IN_MAP.area
        image[y:bottom, x:right] = load_image(IN_MAP.file)[y:bottom, x:right]
        assert IN_MAP.appear_on(image)
        cv2.imwrite(str(output.parent/'watchdog.png'), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
