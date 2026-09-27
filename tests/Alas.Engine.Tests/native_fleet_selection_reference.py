"""Run upstream fleet selection and stage handling with scripted I/O; never connects to a device."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output = [Path(p).resolve() for p in sys.argv[1:]]
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import module.base.timer as timers
        from module.map.map_operation import MapOperation
        from module.logger import logger
        logger.setLevel("CRITICAL")

        class Replay(MapOperation):
            def __init__(self, case):
                self.case, self.frame, self.now = case, 0, 100.
                self.calls, self.clicks, self.delays = [], [], []
                self.config = SimpleNamespace(FLEET_2=case["fleet2"], Fleet_FleetOrder=case["order"])
                self.map_is_hard_mode = False
                self.in_stage_timer = timers.Timer(.5, count=2)
                self.fleet_show_index = self.fleet_current_index = 1
                self.device = SimpleNamespace(screenshot=self.screenshot, click=self.click, sleep=self.sleep)

            def positive(self, name):
                return name in self.case["frames"][min(self.frame, len(self.case["frames"]) - 1)]

            def screenshot(self):
                self.frame += 1
                self.now += self.case["step"]
                if self.frame > 250:
                    raise AssertionError("Native fleet fixture did not terminate")

            def appear(self, button, offset=0, **kwargs):
                bounds = [-offset[0], -offset[1], *offset] if isinstance(offset, tuple) else (
                    [-3, -offset, 3, offset] if offset else [0, 0, 0, 0])
                self.calls.append(dict(asset=button.name, offset=bounds, frame=self.frame))
                return self.positive(button.name)

            def appear_then_click(self, button, **kwargs):
                if self.appear(button, **kwargs):
                    self.click(button)
                    return True
                return False

            def click(self, button):
                self.clicks.append(dict(asset=button.name, frame=self.frame))

            def sleep(self, value):
                seconds = sum(value) / len(value) if isinstance(value, tuple) else value
                self.delays.append(seconds)
                self.now += seconds

            def handle_story_skip(self):
                self.calls.append(dict(asset="$story", offset=[], frame=self.frame))
                return self.positive("$story")

            def is_stage_page_has_entrance(self):
                self.calls.append(dict(asset="$entrance", offset=[], frame=self.frame))
                return self.positive("$entrance")

            def info_bar_count(self):
                self.calls.append(dict(asset="$info", offset=[], frame=self.frame))
                return int(self.positive("$info"))

        results = []
        for case in json.loads(inputs.read_text(encoding="utf-8")):
            driver = Replay(case)
            timers.time = lambda: driver.now
            changed, error = None, None
            try:
                if case["initialize"]:
                    changed = driver.handle_fleet_reverse()
                    if not changed:
                        changed = driver.fleet_set(index=1)
                else:
                    changed = driver.fleet_set(index=case["target"])
            except Exception as exc:
                error = type(exc).__name__
            results.append(dict(changed=changed, error=error, logical=driver.fleet_current_index,
                displayed=driver.fleet_show_index, frames=driver.frame, calls=driver.calls,
                clicks=driver.clicks, delays=driver.delays))
        sources = {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in
                   ["module/map/map_operation.py", "module/handler/enemy_searching.py", "module/handler/info_handler.py"]}
        import cv2
        import numpy as np
        from module.map import assets as map_assets
        from module.handler import assets as handler_assets
        from module.template import assets as templates
        from module.base.utils import load_image
        os.chdir(root)
        for number, formation in [(1, 3), (2, 2)]:
            image = np.zeros((720, 1280, 3), dtype=np.uint8)
            for button in [getattr(map_assets, f"FLEET_NUM_{number}"), map_assets.SWITCH_OVER, handler_assets.IN_MAP]:
                x1, y1, x2, y2 = button.area
                image[y1:y2, x1:x2] = load_image(button.file)[y1:y2, x1:x2]
            template = getattr(templates, f"TEMPLATE_FORMATION_{formation}").image
            x, y, _, _ = handler_assets.MAP_BUFF.area
            h, w = template.shape[:2]
            image[y:y+h, x:x+w] = template
            cv2.imwrite(str(output.parent / f"fleet-{number}.png"), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding="utf-8")


if __name__ == "__main__":
    main()
