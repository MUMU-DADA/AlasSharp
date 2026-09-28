"""Execute native submarine localization with scripted camera/vision I/O; no device access."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output = [Path(arg).resolve() for arg in sys.argv[1:]]
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap, location2node
        from module.map.utils import location_ensure
        from module.logger import logger
        logger.setLevel("CRITICAL")

        class Replay(Fleet):
            def __init__(self, sample):
                self.config = SimpleNamespace(SUBMARINE=sample["enabled"])
                self.map = CampaignMap()
                self.map.shape = sample["shape"]
                self.map.map_data = sample["tiles"]
                self.camera = location_ensure(sample["camera"])
                self.fleet_submarine_location = ()
                self.trace, self.focus = [], []
                self.positive = sample["positive"]
                for cell in sample["observed"]:
                    self.map[location_ensure(cell)].is_submarine = True
                for cell, flag in sample["covered"].items():
                    setattr(self.map[location_ensure(cell)], flag, True)

            def focus_to(self, location, **kwargs):
                self.focus.append(location2node(location))
                self.camera = tuple(int(value) for value in location)

            def convert_global_to_local(self, location):
                name = location2node(location_ensure(location))

                def predict():
                    present = name == self.positive
                    self.trace.append(dict(location=name, camera=location2node(self.camera), present=present))
                    return present

                return SimpleNamespace(predict_submarine=predict)

            def show_submarine(self):
                pass

        results = []
        for sample in json.loads(inputs.read_text(encoding="utf-8")):
            replay = Replay(sample)
            result = replay.find_submarine()
            results.append(dict(location=location2node(result) if result else None,
                                trace=replay.trace, focus=replay.focus,
                                observed=[location2node(grid.location) for grid in replay.map.select(is_submarine=True)]))
        # Use native in_sight itself, independent of the C# requested-camera formula.
        sights = []
        replay = Replay(dict(shape="I7", tiles="\n".join(["-- " * 9] * 7), enabled=True,
                             camera="E4", observed=[], covered={}, positive=None))
        for col in range(9):
            for row in range(7):
                replay.camera = (4, 3)
                replay.in_sight((col, row), sight=(-2, -1, 2, -1))
                sights.append(dict(target=location2node((col, row)), camera=location2node(replay.camera)))
        sources = {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in
                   ["module/map/fleet.py", "module/map/camera.py", "module/map/map_grids.py"]}
    output.write_text(json.dumps(dict(results=results, sights=sights, sources=sources)), encoding="utf-8")


if __name__ == "__main__":
    main()
