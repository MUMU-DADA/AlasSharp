"""Actual upstream combat submarine handler with scripted observations; offline CV fixtures only."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, source, output = [Path(value).resolve() for value in sys.argv[1:4]]
    server_name = sys.argv[4]
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import module.config.server as server
        server.server = server_name
        from module.combat.submarine import SubmarineCall
        import module.base.timer as timers
        from module.logger import logger
        logger.setLevel("CRITICAL")

        class Replay(SubmarineCall):
            def __init__(self):
                self.now = 100.
                self.frame = 0
                self.mask = 0
                self.calls, self.clicks = [], []
                self.device = SimpleNamespace(click=self.click)
                self.submarine_call_timer = timers.Timer(5)
                self.submarine_call_click_timer = timers.Timer(1)

            def appear(self, button, **kwargs):
                assert not kwargs
                self.calls.append(dict(frame=self.frame, asset=button.name))
                bit = ["SUBMARINE_AVAILABLE_CHECK_1", "SUBMARINE_AVAILABLE_CHECK_2",
                       "SUBMARINE_CALLED", "SUBMARINE_READY"].index(button.name)
                return bool(self.mask & (1 << bit))

            def click(self, button):
                self.clicks.append(self.frame)
                assert button.name == "SUBMARINE_READY"

            def appear_then_click(self, button):
                if self.appear(button):
                    self.click(button)
                    return True
                return False

        results = []
        for sample in json.loads(source.read_text(encoding="utf-8")):
            replay = Replay()
            timers.time = lambda: replay.now
            if sample["retryAge"] is not None:
                replay.submarine_call_click_timer.reset()
                replay.now += sample["retryAge"]
            started = replay.now
            replay.submarine_call_reset()
            handled, stopped = [], []
            for index, step in enumerate(sample["steps"]):
                replay.frame = index + 1
                replay.now = started + step["seconds"]
                replay.mask = step["mask"]
                handled.append(bool(replay.handle_submarine_call(sample["mode"])))
                stopped.append(bool(replay.submarine_call_flag))
            results.append(dict(calls=replay.calls, clicks=replay.clicks, handled=handled, stopped=stopped))

        import cv2
        import numpy as np
        from module.base.base import ModuleBase
        from module.base.utils import load_image
        from module.combat import assets
        os.chdir(root)
        buttons = [getattr(assets, name) for name in ["SUBMARINE_AVAILABLE_CHECK_1",
            "SUBMARINE_AVAILABLE_CHECK_2", "SUBMARINE_CALLED", "SUBMARINE_READY"]]
        pixels = []
        for index, button in enumerate(buttons):
            for present in [False, True]:
                image = np.zeros((720, 1280, 3), dtype=np.uint8)
                if present:
                    x1, y1, x2, y2 = button.area
                    image[y1:y2, x1:x2] = load_image(button.file)[y1:y2, x1:x2]
                native = ModuleBase.__new__(ModuleBase)
                native.device = SimpleNamespace(image=image, stuck_record_add=lambda _: None)
                matched = native.appear(button)
                filename = f"submarine-call-{server_name}-{index}-{int(present)}.png"
                cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
                pixels.append(dict(file=filename, asset=button.name, matched=bool(matched), positive=present))
        sources = {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
                   for name in ["module/combat/submarine.py", "module/combat/combat.py", "module/base/timer.py"]}
    output.write_text(json.dumps(dict(results=results, pixels=pixels, sources=sources)), encoding="utf-8")


if __name__ == "__main__":
    main()
