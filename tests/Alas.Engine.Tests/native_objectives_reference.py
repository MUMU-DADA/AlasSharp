"""Offline upstream map objective, progression and target-selection oracle."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output = [Path(p).resolve() for p in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        from module.handler import fast_forward as native
        from module.map.map import Map
        from module.map.map_base import CampaignMap
        from module.logger import logger
        from module.base.utils import location2node
        logger.setLevel("CRITICAL")
        samples = json.loads(inputs.read_text(encoding="utf-8"))

        class Objective(native.FastForwardHandler):
            def __init__(self, case):
                self.case = case
                self.config = SimpleNamespace(Campaign_Name="1-1", STAR_REQUIRE_3=case["star"],
                    MAP_HAS_MAP_STORY=case["story"], StopCondition_MapAchievement=case["achievement"])

            def get_map_clear_percentage(self):
                return self.case["percentage"]

            def _is_map_star_active(self, button):
                return self.case["stars"][int(button.name[-1])-1]

            def appear(self, button, **kwargs):
                return self.case["safe"] if button.name == "MAP_GREEN" else True

            def image_color_count(self, *args, **kwargs):
                return True

            def map_show_info(self):
                pass

        objectives = []
        for sample in samples["objectives"]:
            replay = Objective(sample)
            replay.map_get_info()
            objectives.append(dict(clearAll=bool(replay.config.MAP_CLEAR_ALL_THIS_TIME),
                story=replay.config.MAP_HAS_MAP_STORY, stop=replay.triggered_map_stop()))

        progression = []
        for sample in samples["progression"]:
            replay = Objective(dict(star=3, story=False, achievement="non_stop"))
            replay.config.Campaign_Name = sample["name"]
            replay.config.Campaign_Event = sample["folder"]
            replay.config.STAGE_INCREASE_AB = sample["across"]
            replay.config.STAGE_INCREASE_CUSTOM = sample["custom"]
            native.map_files = lambda folder, files=sample["files"]: files
            progression.append(replay.campaign_name_increase(sample["name"]))

        class Target(Map):
            def __init__(self, sample):
                self.config = SimpleNamespace(MAP_HAS_SIREN=sample["siren"], MAP_HAS_FORTRESS=sample["fortress"], FLEET_2=sample["fleet2"])
                self.map = CampaignMap("offline")
                self.map.shape = "F1"
                self.map.map_data = "-- -- -- -- -- --"
                self.map.load_map_data(False)
                for grid, patch in zip(self.map, sample["cells"]):
                    for field, value in patch.items():
                        setattr(grid, field, value)
                self.selected = None
                self.candidates = []

            def select_grids(self, grids, **kwargs):
                result = super().select_grids(grids, **kwargs)
                keys = kwargs.get("sort", ("weight", "cost"))
                if result:
                    best = tuple(getattr(result[0], key) for key in keys)
                    # SelectedGrids.add uses object-hash sets: equal-priority ties
                    # have no cross-process order. Compare the native minimum set.
                    self.candidates = [location2node(g.location) for g in result
                        if tuple(getattr(g, key) for key in keys) == best]
                return result

            def clear_chosen_enemy(self, grid, expected=""):
                self.selected = location2node(grid.location)

            def show_select_grids(self, *args, **kwargs):
                pass

        targeting = []
        for sample in samples["targeting"]:
            replay = Target(sample)
            replay.clear_siren()
            siren = replay.candidates
            replay.selected = None
            replay.candidates = []
            replay.clear_any_enemy(sort=("cost_2",))
            targeting.append(dict(siren=siren, any=replay.candidates))
        sources = {path: hashlib.sha256((root / path).read_bytes()).hexdigest() for path in
            ["module/handler/fast_forward.py", "module/map/map.py", "module/map/fleet.py", "module/map/map_operation.py"]}
        # Synthetic screenshots made from native assets; no device or account input.
        import numpy as np
        from PIL import Image
        from module.map.assets import MAP_PREPARATION
        from module.ui.assets import CAMPAIGN_CHECK
        from module.template.assets import TEMPLATE_STAGE_CLEAR
        os.chdir(root)
        preparation = np.zeros((720, 1280, 3), dtype=np.uint8)
        stage = preparation.copy()
        def paste(image, button):
            button = button.split_server()['cn']
            x, y, r, b = button.area
            source = np.array(Image.open(button.file).convert('RGB'))
            image[y:b, x:r] = source[y:b, x:r]
        paste(preparation, MAP_PREPARATION)
        x, y, r, b = native.MAP_CLEAR_PERCENTAGE.split_server()['cn'].area
        preparation[y:b, x:x + int((r-x)*.99)] = (231, 170, 82)
        paste(stage, CAMPAIGN_CHECK)
        template = np.array(Image.open(TEMPLATE_STAGE_CLEAR.split_server()['cn'].file).convert('RGB'))
        stage[300:300+template.shape[0], 300:300+template.shape[1]] = template
        Image.fromarray(preparation).save(output.parent / 'objective-preparation.png')
        Image.fromarray(stage).save(output.parent / 'objective-stage.png')
        output.write_text(json.dumps(dict(objectives=objectives, progression=progression, targeting=targeting, sources=sources)), encoding="utf-8")


if __name__ == "__main__":
    main()
