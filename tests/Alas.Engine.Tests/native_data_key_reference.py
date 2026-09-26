"""Offline oracle: run the actual upstream DataKey task with scripted observation boundaries."""
import contextlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, server_name, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve()
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import module.config.server as server
        server.server = server_name
        from module.freebies import data_key as source
        from module.ocr.ocr import Ocr
        from module.logger import logger
        logger.setLevel("CRITICAL")

        actor = [None]
        def read(ocr, image, direct_ocr=False):
            actor[0].events.append(["ocr"])
            return ocr.after_process(actor[0].sample["text"])
        Ocr.ocr = read

        class Reference(source.DataKey):
            def ui_ensure(self, page, **kwargs):
                self.events.append(["navigate", page.name])
            def appear(self, button, offset=0, interval=0, similarity=0.85, threshold=10):
                area = ([-offset[0], -offset[1], *offset] if isinstance(offset, tuple)
                        else [-3, -offset, 3, offset] if offset else [0, 0, 0, 0])
                self.events.append(["appear", button.name, area, interval, similarity, threshold])
                return button.name in self.sample["frames"][self.index]
            def handle_popup_confirm(self, additional="", **kwargs):
                self.events.append(["confirm"])
                return "confirm" in self.sample["frames"][self.index]
            def interval_clear(self, button, interval=3):
                for item in button if isinstance(button, (list, tuple)) else [button]:
                    self.events.append(["clear", item.name])
            def data_key_collect(self):
                value = super().data_key_collect()
                self.collected = value
                return value

        collect = [["DATA_KEY_COLLECT", "GET_ITEMS_1"], ["GET_ITEMS_1"], ["confirm"],
                   ["CAMPAIGN_MENU_GOTO_WAR_ARCHIVES"], ["WAR_ARCHIVES_CHECK", "DATA_KEY_COLLECTED"]]
        samples = []
        for text in ("0/30", "29/30", "30/30", "31/30", "B/I0", "", "unreadable"):
            for force in (False, True):
                samples.append(dict(name=f"inventory-{text}-{force}", text=text, force=force, frames=collect, fail=False))
        samples.extend([
            dict(name="already", text="", force=False, frames=[["DATA_KEY_COLLECTED"]], fail=False),
            dict(name="click-failure", text="0/30", force=False, frames=collect, fail=True),
            dict(name="never-completes", text="0/30", force=False, frames=[[], []], fail=False),
            dict(name="delayed-completion", text="0/30", force=False, frames=[[], [], ["WAR_ARCHIVES_CHECK", "DATA_KEY_COLLECTED"]], fail=False),
        ])
        results = []
        for sample in samples:
            instance = Reference.__new__(Reference)
            instance.sample, instance.events, instance.index, instance.collected = sample, [], 0, None
            instance.config = SimpleNamespace(DataKey_ForceCollect=sample["force"])
            def screenshot():
                instance.events.append(["screenshot"])
                instance.index += 1
                if instance.index >= len(sample["frames"]):
                    raise TimeoutError("scripted frames exhausted")
            def click(button):
                instance.events.append(["click", button.name])
                if sample["fail"]:
                    raise IOError("synthetic click failure")
            instance.device = SimpleNamespace(image=None, screenshot=screenshot, click=click)
            actor[0] = instance
            error = None
            try:
                instance.run()
            except Exception as failure:
                error = type(failure).__name__
            results.append(dict(sample=sample, events=instance.events, collected=instance.collected, error=error))
    output.write_text(json.dumps(results, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
