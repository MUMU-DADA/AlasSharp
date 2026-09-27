"""Offline oracle: execute unmodified upstream Switch/StrategyHandler with scripted observations."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output = map(lambda p: Path(p).resolve(), sys.argv[1:])
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import module.base.timer as timers
        from module.ui.switch import Switch
        from module.handler.strategy import StrategyHandler
        from module.handler import assets
        from module.logger import logger
        logger.setLevel("CRITICAL")
        original_time = timers.time

        class Driver(StrategyHandler):
            def __init__(self, case):
                self.case, self.frame, self.now = case, 0, 100.0
                self.clicks, self.calls, self.intervals = [], [], {}
                self.device = SimpleNamespace(screenshot=self.screenshot, click=self.click)
                self.config = SimpleNamespace(
                    Fleet_Fleet1Formation=case.get("formation", "double_line"),
                    Fleet_Fleet2Formation=case.get("formation", "double_line"),
                    Submarine_Fleet=case.get("submarine", 0),
                    Submarine_Mode=case.get("mode", "do_not_use"))
                self.fleet_1_formation_fixed = self.fleet_2_formation_fixed = False

            def screenshot(self):
                self.frame += 1
                self.now += self.case["step"]
                if self.frame > 300:
                    raise AssertionError("Native reference did not finish within the fixture")

            def click(self, button):
                self.clicks.append(dict(asset=button.name, frame=self.frame))

            def appear(self, button, offset=0, interval=0):
                if isinstance(offset, int):
                    bounds = [-3, -offset, 3, offset] if offset else [0, 0, 0, 0]
                else:
                    bounds = [-offset[0], -offset[1], offset[0], offset[1]]
                self.calls.append(dict(asset=button.name, offset=bounds, interval=interval, frame=self.frame))
                if interval:
                    timer = self.intervals.setdefault(button.name, timers.Timer(interval))
                    if not timer.reached():
                        return False
                present = button.name in self.case["frames"][min(self.frame, len(self.case["frames"]) - 1)]
                if present and interval:
                    timer.reset()
                return present

            def appear_then_click(self, button, offset=0, interval=0):
                if self.appear(button, offset, interval):
                    self.click(button)
                    return True
                return False

            def _strategy_get_from_map_buff(self):
                return self.case.get("buff") or "unknown"

        results = []
        for case in json.loads(inputs.read_text(encoding="utf-8")):
            driver = Driver(case)
            timers.time = lambda: driver.now
            if case["kind"] == "switch":
                switch = Switch(is_selector=case["selector"], offset=(100, 200))
                switch.add_state("on", assets.SUBMARINE_VIEW_ON)
                switch.add_state("off", assets.SUBMARINE_VIEW_OFF)
                changed = switch.set(case["expected"], driver)
                repeated = False
            else:
                changed = driver.handle_strategy(case["fleet"])
                repeated = driver.handle_strategy(case["fleet"])
            results.append(dict(changed=changed, repeated=repeated, frames=driver.frame,
                                clicks=driver.clicks, calls=driver.calls))
        timers.time = original_time

        # Pure pixel oracle uses real Template.match and real map-buff cropping.
        import cv2
        import numpy as np
        from module.template import assets as templates
        os.chdir(root)
        pixels = []
        for index, choices in enumerate([[], [1], [2], [3], [1, 2, 3]]):
            image = np.zeros((720, 1280, 3), dtype=np.uint8)
            x, y, right, bottom = assets.MAP_BUFF.area
            for choice in choices:
                template = getattr(templates, f"TEMPLATE_FORMATION_{choice}").image
                h, w = template.shape[:2]
                if x + w > right or y + h > bottom:
                    raise AssertionError("Formation fixture does not fit the native map buff")
                image[y:y+h, x:x+w] = template
                x += w + 3
            file = output.parent / f"formation-{index}.png"
            cv2.imwrite(str(file), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
            reference = SimpleNamespace(image_crop=lambda button, copy:
                                        image[button.area[1]:button.area[3], button.area[0]:button.area[2]])
            result = StrategyHandler._strategy_get_from_map_buff(reference)
            pixels.append(dict(file=file.name, expected=None if result == "unknown" else result))
        sources = {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in
                   ["module/ui/switch.py", "module/handler/strategy.py"]}
    output.write_text(json.dumps(dict(results=results, pixels=pixels, sources=sources)), encoding="utf-8")


if __name__ == "__main__":
    main()
