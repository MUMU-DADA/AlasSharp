"""Run actual CampaignMap.update/fixup methods against whole scripted visual views."""
import contextlib
import copy
import itertools
import json
import os
from pathlib import Path
import random
import sys
from types import SimpleNamespace


def main():
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        from module.map.map_base import CampaignMap
        from module.map_detection.grid_info import GridInfo
        from module.base.utils import node2location, location2node
        from module.logger import logger
        logger.setLevel("CRITICAL")
        fields = sorted(k for k, v in vars(GridInfo).items() if not k.startswith("_") and
                        (isinstance(v, (bool, int, float, property)) or k == "enemy_genre"))
        observed = ["is_submarine", "is_caught_by_siren", "is_fleet", "is_current_fleet", "is_boss", "is_siren",
                    "is_enemy", "is_mystery", "is_ammo", "is_missile_attack"]
        rng = random.Random(84127)

        def item(x, y, **state):
            return dict(local=[x, y], state=state)

        def view(cells, mode="normal", camera="A1", center=(0, 0)):
            return dict(cells=cells, mode=mode, camera=camera, center=list(center))

        cases = []
        for mode, failures, ignored in itertools.product(["normal", "init", "carrier", "movable", "decoy"], range(3), [False, True]):
            cells = [item(0, 1, is_fleet=True, is_current_fleet=True), item(1, 1, is_fleet=True, is_current_fleet=True),
                     item(2, 1, is_fleet=True, is_enemy=True, enemy_scale=3, enemy_genre="Main")]
            # The first conflict writes submarine before rejecting boss. The second rejects a land fleet.
            if failures:
                cells.append(item(0, 0, is_submarine=True, is_boss=True))
            if failures == 2:
                cells.append(item(3, 0, is_fleet=True))
            cases.append(dict(name=f"threshold-{mode}-{failures}-{ignored}", shape="D3", tiles="__ __ MB ++\n-- SP ME MS\nMA MM -- --", before=[],
                              ignored=[dict(cell="A1", state=dict(is_boss=True))] if ignored else [],
                              views=[view(cells, mode), view([item(2, 0, is_boss=True)])]))
        cases.extend([
            dict(name="offset-ignore", shape="D3", tiles="__ __ MB ++\n-- SP ME MS\nMA MM -- --", before=[],
                 ignored=[dict(cell="C2", state=dict(enemy_scale=1, enemy_genre="Enemy"))],
                 views=[view([item(1, 1, is_enemy=True, enemy_scale=1, enemy_genre="Enemy"), item(3, 3, is_boss=True)], camera="C2", center=(1, 1)),
                        view([item(0, 0, is_enemy=True, enemy_scale=2, enemy_genre="Enemy")], camera="C2"),
                        view([item(0, 0, is_fleet=True), item(4, 1, is_fleet=True)], camera="A1", center=(1, 1))]),
            dict(name="empty-view", shape="D3", tiles="__ __ MB ++\n-- SP ME MS\nMA MM -- --", before=[dict(cell="C2", state=dict(is_fleet=True, is_enemy=True))],
                 ignored=[], views=[view([], "normal"), view([], "init")]),
        ])
        for index in range(500):
            tokens = [rng.choice(["--", "++", "SP", "__", "ME", "Me", "MB", "MM", "MA", "MS"]) for _ in range(12)]
            patches = []
            for x, y in itertools.product(range(4), range(3)):
                if rng.randrange(4) == 0:
                    flags = {flag: bool(rng.randrange(2)) for flag in observed + ["is_fortress", "is_carrier", "is_movable", "may_enemy", "may_boss", "may_siren"]}
                    flags.update(enemy_scale=rng.randrange(4), enemy_genre=rng.choice([None, "Enemy", "Main", "Siren_DD"]))
                    patches.append(dict(cell=location2node((x, y)), state=flags))
            views = []
            for number in range(3):
                cells = []
                for y in range(3):
                    for x in range(4):
                        if rng.randrange(3) == 0:
                            flags = {flag: True for flag in observed if rng.randrange(7) == 0}
                            flags.update(enemy_scale=rng.randrange(4), enemy_genre=rng.choice([None, "Enemy", "Carrier", "Siren_DD"]))
                            cells.append(item(x, y, **flags))
                views.append(view(cells, rng.choice(["normal", "init", "carrier", "movable", "decoy"]),
                                  camera=location2node((rng.randrange(4), rng.randrange(3))), center=(rng.randrange(3), rng.randrange(2))))
            cases.append(dict(name=f"random-{index}", shape="D3", tiles="\n".join(" ".join(tokens[y * 4:y * 4 + 4]) for y in range(3)), before=patches,
                              ignored=[dict(cell="C2", state=dict(is_siren=True)), dict(cell="B1", state=dict(enemy_scale=1, enemy_genre="Enemy"))], views=views))
        results = []
        for case in cases:
            mapping = CampaignMap("reference")
            mapping.shape, mapping.map_data = case["shape"], case["tiles"]
            for patch in case["before"]:
                for key, value in patch["state"].items():
                    setattr(mapping[node2location(patch["cell"])], key, value)
            for ignored in case["ignored"]:
                mapping.ignore_prediction(ignored["cell"], **ignored["state"])
            outputs = []
            for sample in case["views"]:
                grids = {}
                for local in sample["cells"]:
                    grid = GridInfo()
                    grid.location = tuple(local["local"])
                    for key, value in local["state"].items():
                        setattr(grid, key, value)
                    grids[grid.location] = grid
                view_object = SimpleNamespace(grids=grids, center_loca=sample["center"])
                camera = node2location(sample["camera"])
                failures, applied, ignored, outside = 0, 0, [], []
                for grid in grids.values():
                    point = tuple(camera[i] - sample["center"][i] + grid.location[i] for i in range(2))
                    if point not in mapping:
                        outside.append([point[0] + 1, point[1] + 1])
                    elif mapping.ignore_prediction_match(point, grid):
                        ignored.append(location2node(point))
                    else:
                        applied += 1
                        failures += not copy.copy(mapping[point]).merge(grid, sample["mode"])
                accepted = mapping.update(view_object, camera, sample["mode"])
                outputs.append(dict(accepted=accepted, failures=failures, applied=applied if accepted else 0, ignored=ignored, outside=outside,
                                    cells=[dict(cell=location2node(g.location), values={k: getattr(g, k) for k in fields}) for g in mapping]))
            results.append(dict(sample=case, outputs=outputs))
    output.write_text(json.dumps(results, separators=(",", ":")), encoding="utf-8")


if __name__ == "__main__":
    main()
