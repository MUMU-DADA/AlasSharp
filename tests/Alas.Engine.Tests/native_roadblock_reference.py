"""Offline native Fleet.brute_find_roadblocks oracle; no device or business worker dependency."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys


def main():
    root, inputs, output = [Path(p).resolve() for p in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap
        from module.base.utils import node2location, location2node
        from module.logger import logger
        logger.setLevel("CRITICAL")

        class Replay(Fleet):
            def __init__(self, sample):
                self.map = CampaignMap("offline")
                self.map.shape = sample["shape"]
                self.map.map_data = sample["tiles"]
                self.map.load_map_data(False)
                self.map.grid_connection_initial()
                for a, b in sample["walls"]:
                    a, b = tuple(node2location(a)), tuple(node2location(b))
                    self.map.grid_connection[a].discard(b)
                    self.map.grid_connection[b].discard(a)
                for a, b in sample["portals"]:
                    self.map.grid_connection[tuple(node2location(a))].add(tuple(node2location(b)))
                for patch in sample["patches"]:
                    for field, value in patch["flags"].items():
                        setattr(self.map[tuple(node2location(patch["cell"]))], field, value)
                self.fleet_1_location = tuple(node2location(sample["fleet1"]))
                self.fleet_2_location = tuple(node2location(sample["fleet2"]))
                self.fleet_current_index = sample["active"]
                self.find_path_initial()

            def find_path_initial(self):
                self.map.find_path_initial(self.fleet_current, has_ambush=False)

        results = []
        for sample in json.loads(inputs.read_text(encoding="utf-8")):
            replay = Replay(sample)
            before = [(g.cost, g.connection) for g in replay.map]
            result = replay.brute_find_roadblocks(replay.map[tuple(node2location(sample["target"]))], sample["fleet"])
            results.append(dict(reachable=result is not None,
                enemies=[] if result is None else [location2node(g.location) for g in result],
                mutated=before != [(g.cost, g.connection) for g in replay.map]))
        output.write_text(json.dumps(dict(results=results, source=hashlib.sha256(
            (root / "module/map/fleet.py").read_bytes()).hexdigest())), encoding="utf-8")


if __name__ == "__main__":
    main()
