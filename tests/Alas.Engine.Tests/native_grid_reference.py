"""Offline GridInfo oracle. Calls native merge/decode/wipe/reset, with no visual or device host."""
import contextlib
import itertools
import json
import os
from pathlib import Path
import random
import sys


def main():
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        from module.map_detection.grid_info import GridInfo
        from module.map.map_grids import SelectedGrids
        tokens = ["--", "++", "SP", "__", "ME", "Me", "MB", "MM", "MA", "MS"]
        modes = ["normal", "init", "carrier", "movable", "decoy"]
        fields = sorted(k for k, v in vars(GridInfo).items() if not k.startswith("_") and
                        (isinstance(v, (bool, int, float, property)) or k == "enemy_genre"))
        observed = ["is_submarine", "is_caught_by_siren", "is_fleet", "is_current_fleet", "is_boss", "is_siren",
                    "is_enemy", "is_mystery", "is_ammo", "is_missile_attack"]
        observations = [{}] + [{flag: True, "enemy_scale": 2, "enemy_genre": "Light"} for flag in observed]
        observations += [dict(is_fleet=True, is_enemy=True, is_current_fleet=True, enemy_scale=3, enemy_genre="Enemy"),
                         dict(is_siren=True, enemy_genre="Siren_elite_DD_extra"), dict(is_siren=True, enemy_genre="Siren_A")]
        starts = [{}, dict(is_enemy=True, enemy_scale=2, enemy_genre="Main"),
                  dict(is_carrier=True, enemy_scale=1, enemy_genre="Carrier"),
                  dict(is_movable=True, is_fleet=True), dict(is_fortress=True, is_cleared=True)]
        samples = [dict(token=token, mode=mode, before=before, info=info, reload="MS")
                   for token, mode, before, info in itertools.product(tokens, modes, starts, observations)]
        rng = random.Random(53779)
        booleans = [k for k in fields if isinstance(getattr(GridInfo, k), bool)]
        for _ in range(600):
            before = {k: rng.choice([False, True]) for k in booleans}
            before.update(enemy_scale=rng.randrange(4), enemy_genre=rng.choice([None, "", "Enemy", "Main", "Siren_elite_DD_extra"]),
                          cost=rng.choice([0, 19, 20, 9998, 9999]), cost_1=rng.choice([0, 9999]), cost_2=rng.choice([0, 9999]), weight=3.5)
            info = {k: rng.choice([False, True]) for k in observed}
            info.update(enemy_scale=rng.randrange(4), enemy_genre=rng.choice([None, "", "Enemy", "Light", "Siren_A", "Siren_elite_DD_extra"]))
            samples.append(dict(token=rng.choice(tokens), mode=rng.choice(modes), before=before, info=info, reload=rng.choice(tokens)))
        results = []
        for sample in samples:
            grid, trigger, block, info = GridInfo(), GridInfo(), GridInfo(), GridInfo()
            grid.location, trigger.location, block.location = (0, 0), (1, 0), (2, 0)
            grid.decode(sample["token"])
            for key, value in sample["before"].items():
                setattr(grid, key, value)
            trigger.is_mechanism_trigger = block.is_mechanism_block = True
            if grid.is_mechanism_trigger:
                grid.mechanism_trigger = SelectedGrids([grid, trigger])
                grid.mechanism_block = SelectedGrids([block])
            for key, value in sample["info"].items():
                setattr(info, key, value)
            def snapshot():
                return dict(values={k: getattr(grid, k) for k in fields}, covered=grid.covered_grid(),
                            trigger=trigger.is_mechanism_trigger, block=block.is_mechanism_block,
                            trigger_group=grid.mechanism_trigger is not None, block_group=grid.mechanism_block is not None,
                            distance=grid.distance_to(block))
            frames = [snapshot()]
            merged = grid.merge(info, mode=sample["mode"])
            frames.append(snapshot())
            grid.decode(sample["reload"])
            frames.append(snapshot())
            grid.wipe_out()
            frames.append(snapshot())
            grid.reset()
            frames.append(snapshot())
            results.append(dict(sample=sample, merged=merged, frames=frames))
    output.write_text(json.dumps(results, separators=(",", ":")), encoding="utf-8")


if __name__ == "__main__":
    main()
