"""Execute actual upstream recovery methods with image/device boundaries observed offline."""
import ast
import contextlib
import importlib
import json
import os
from pathlib import Path
import random
import sys
from types import SimpleNamespace


def main():
    root, server_name, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve()
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import numpy as np
        import imageio.v2 as imageio
        import module.config.server as server
        server.server = server_name
        import module.base.timer as timers
        from module.base.button import Button
        from module.base.utils import crop
        from module.ui.ui import UI
        from module.ui.page import Page
        from module.handler.info_handler import InfoHandler
        from module.logger import logger
        logger.setLevel("CRITICAL")
        identities, assets = {}, {}
        for path in sorted((root / "module").rglob("assets.py")):
            name = path.relative_to(root).as_posix()[:-3].replace("/", ".")
            for key, asset in vars(importlib.import_module(name)).items():
                if isinstance(asset, Button):
                    identities[id(asset)] = name + "." + key
                    assets[name + "." + key] = asset
        ui_module = importlib.import_module("module.ui.ui")
        info_module = importlib.import_module("module.handler.info_handler")
        now, actor = [1000.0], [None]
        timers.time = lambda: now[0]
        def key(button):
            return identities.get(id(button), "dynamic")
        def offset_values(offset):
            if offset is True:
                offset = 30
            if isinstance(offset, tuple):
                return [-offset[0], -offset[1], *offset] if len(offset) == 2 else list(offset)
            return [-3, -offset, 3, offset] if offset else [0, 0, 0, 0]
        Button.match = lambda button, image, **kw: key(button) in actor[0].positive
        Button.appear_on = lambda button, image, **kw: key(button) in actor[0].positive
        def luma(button, image, offset=30, similarity=0.85):
            actor[0].events.append(["appear", key(button), offset_values(offset), 0, similarity, 10, "Luma"])
            return key(button) in actor[0].positive
        Button.match_luma = luma

        class Reference(UI):
            def appear(self, button, offset=0, interval=0, similarity=0.85, threshold=10):
                self.events.append(["appear", key(button), offset_values(offset), interval, similarity, threshold, "Color"])
                return super().appear(button, offset=offset, interval=interval, similarity=similarity, threshold=threshold)
            def interval_reset(self, button, interval=3):
                if isinstance(button, (list, tuple)):
                    for item in button:
                        self.interval_reset(item, interval)
                    return
                self.events.append(["reset", key(button)])
                return super().interval_reset(button, interval)
            def interval_clear(self, button, interval=3):
                self.events.append(["clear", key(button)])
                return super().interval_clear(button, interval)
            def _is_story_black(self):
                self.events.append(["color"])
                return super()._is_story_black()
            def _story_option_buttons_2(self):
                self.events.append(["bands"])
                return super()._story_option_buttons_2()
            def image_crop(self, area, copy=True):
                return crop(self.device.image, area, copy=copy)

        def create(options):
            instance = Reference.__new__(Reference)
            instance.events, instance.positive, instance.interval_timer = [], set(), {}
            instance.config = SimpleNamespace(BUTTON_OFFSET=30, STORY_OPTION=options.get("selection", 0),
                STORY_ALLOW_SKIP=options.get("allowSkip", True), Campaign_Event=options.get("campaign", ""),
                Emulator_ControlMethod="ADB")
            instance.map_is_threat_safe = options.get("threatSafe", False)
            for name, seconds, count in (("_hot_fix_check_wait", 6, 0), ("story_popup_timeout", 10, 20),
                ("_story_confirm", 0.5, 1), ("_story_option_timer", 2, 0), ("_story_option_confirm", 0.3, 0)):
                setattr(instance, name, timers.Timer(seconds, count))
            instance._story_option_record, instance._opsi_reset_fleet_preparation_click = 0, 0
            def click(button):
                instance.events.append(["click", key(button)] if id(button) in identities else ["click_area", [int(v) for v in button.button]])
            def screenshot():
                instance.events.append(["screenshot"])
                if instance.disappear:
                    instance.positive.clear()
            def delay(seconds):
                instance.events.append(["delay", seconds]); now[0] += seconds
            def running():
                instance.events.append(["running"]); return instance.running
            instance.device = SimpleNamespace(image=np.full((720, 1280, 3), 128, dtype=np.uint8),
                stuck_record_add=lambda b: None, click=click, screenshot=screenshot, sleep=delay,
                app_is_running=running, app_stop=lambda: instance.events.append(["stop"]))
            instance.running, instance.disappear = True, False
            actor[0] = instance
            return instance

        methods = {"ui_additional", "ui_page_os_popups", "ui_page_main_popups", "handle_idle_page", "handle_popup_confirm",
                   "handle_guild_popup_cancel", "handle_popup_single", "handle_popup_single_white", "handle_urgent_commission",
                   "story_skip", "_is_story_black"}
        candidates = set()
        for module, relative in ((ui_module, "module/ui/ui.py"), (info_module, "module/handler/info_handler.py")):
            for node in ast.walk(ast.parse((root / relative).read_text(encoding="utf-8"))):
                if isinstance(node, ast.FunctionDef) and node.name in methods:
                    for reference in ast.walk(node):
                        value = getattr(module, reference.id, None) if isinstance(reference, ast.Name) else None
                        if isinstance(value, Button):
                            candidates.add(key(value))
        cases = []
        def run(name, frames, options=None, operation="additional"):
            options = options or {}
            instance = create(options)
            outputs = []
            for frame in frames:
                now[0] += frame.get("advance", 1)
                instance.positive = set(frame.get("positive", []))
                instance.running, instance.disappear = frame.get("running", True), frame.get("disappear", False)
                instance.device.image[:] = frame.get("gray", 128)
                for n in range(frame.get("options", 0)):
                    instance.device.image[170 + 100 * n:220 + 100 * n, 330:980] = 247
                instance.events = []
                try:
                    value = instance.handle_story_skip() if operation == "story" else instance.ui_additional(get_ship=frame.get("getShip", True))
                    error = None
                except Exception as failure:
                    value, error = None, type(failure).__name__
                outputs.append(dict(value=value, error=error, events=instance.events))
            cases.append(dict(name=name, operation=operation, options=options, frames=frames, outputs=outputs))
        run("none", [{}])
        for candidate in sorted(candidates):
            for get_ship in (False, True):
                run(candidate + str(get_ship), [dict(positive=[candidate], getShip=get_ship)] * 3)
        pairs = [("module.handler.assets.POPUP_CANCEL", "module.handler.assets.POPUP_CONFIRM"),
                 ("module.handler.assets.GUILD_POPUP_CONFIRM", "module.handler.assets.GUILD_POPUP_CANCEL")]
        for pair in pairs:
            run("pair", [dict(positive=pair)] * 4)
        rng = random.Random(6285)
        for n in range(50):
            run(f"priority-{n}", [dict(positive=rng.sample(sorted(candidates), 5))])
        reset = "module.os_handler.assets.RESET_FLEET_PREPARATION"
        run("reset-limit", [dict(positive=[reset], advance=4)] * 7)
        mission = "module.handler.assets.GET_MISSION"
        for running, login in ((True, False), (False, False), (True, True)):
            run("hotfix", [dict(positive=[mission]), dict(advance=3, running=running,
                positive=["module.handler.assets.LOGIN_CHECK"] if login else [])])
        run("withdraw-disappears", [dict(positive=["module.map.assets.WITHDRAW"], disappear=True)])
        for selection in (0, 1, -1, -4, 8):
            for allow_skip in (False, True):
                # Keep multi-step time away from a float epoch equality; exact boundaries are tested separately.
                run("story-options", [dict(positive=["module.handler.assets.STORY_SKIP_3"], options=3, advance=0.41)] * 10,
                    dict(selection=selection, allowSkip=allow_skip), "story")
        for campaign in ("", "event_20201012_cn"):
            run("story-threat", [dict(positive=["module.handler.assets.STORY_CLOSE"])], dict(threatSafe=True, campaign=campaign), "story")
        run("story-black", [dict(gray=0, positive=["module.handler.assets.STORY_LETTERS_ONLY"])] * 3, operation="story")

        option_images = []
        instance = create({})
        for count in range(4):
            for noise in (False, True):
                image = np.zeros((720, 1280, 3), dtype=np.uint8)
                for n in range(count):
                    image[170 + n * 100:220 + n * 100, 330:980] = 247
                    if noise:
                        image[185 + n * 100:187 + n * 100, 330:340] = 0
                instance.device.image = image
                rectangles = [[int(v) for v in b.button] for b in instance._story_option_buttons_2()]
                name = f"options-{server_name}-{count}-{noise}.png"
                imageio.imwrite(output.parent / name, image)
                option_images.append(dict(image=name, rectangles=rectangles))
        resets = []
        for page in Page.iter_pages():
            for button in page.links.values():
                instance = create({})
                instance.ui_button_interval_reset(button)
                resets.append(dict(asset=key(button), events=instance.events,
                    timers={k: v.limit for k, v in instance.interval_timer.items()}))
    output.write_text(json.dumps(dict(cases=cases, options=option_images, resets=resets)), encoding="utf-8")


if __name__ == "__main__":
    main()
