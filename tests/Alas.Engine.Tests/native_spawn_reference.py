"""Offline oracle: execute actual Fleet initialization and CampaignMap spawn methods.

This test imports upstream business code only to obtain reference results; it is
never a runtime dependency of Alas.Engine and performs no device actions.
"""
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
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        from module.base.utils import location2node, node2location
        from module.map.map_base import CampaignMap
        from module.map.fleet import Fleet
        from module.logger import logger
        logger.setLevel("CRITICAL")
        fields = ["is_land", "is_spawn_point", "is_submarine_spawn_point", "may_enemy", "may_boss",
                  "may_mystery", "may_siren", "may_ammo", "may_ambush", "is_enemy", "is_boss", "is_siren",
                  "is_mystery", "is_fleet", "is_current_fleet", "is_fortress", "may_bouncing_enemy", "is_mechanism_block", "may_carrier"]

        def sample(name, **extra):
            result = dict(name=name, shape="D3", tiles="ME MB MM MS\nSP -- -- --\n-- -- -- --",
                          loop="ME MB -- --\nSP -- -- --\n-- -- -- --", waves=[dict(battle=0, enemy=1, mystery=1, siren=1), dict(battle=2, enemy=1, boss=1)],
                          loop_waves=[dict(battle=0, enemy=2), dict(battle=4, boss=1)], covered=["A1", "B1", "C1", "D1"],
                          clear=False, poor=False, fortress=False, bouncing=False, fortress_enemies=[], fortress_blocks=[],
                          bouncing_routes=[], before=[], patches=[], battle=0, mystery=0, siren=0, carrier=0, mode="normal", reload=[])
            result.update(extra)
            return result

        def patch(cell, **state):
            return dict(cell=cell, state=state)

        samples = []
        # Full mode/clear/poor/enable matrix; deliberately sparse battle labels.
        for mode, clear, poor, fortress, bouncing in itertools.product(
                ["normal", "init", "carrier", "movable", "decoy"], [False, True], [False, True], [False, True], [False, True]):
            samples.append(sample(f"mechanisms-{len(samples)}", mode=mode, clear=clear, poor=poor, fortress=fortress, bouncing=bouncing,
                                  fortress_enemies=["C2"], fortress_blocks=["D2"], bouncing_routes=[["B2", "B3"], []],
                                  patches=[patch("A3", is_fleet=True, is_current_fleet=True), patch("D2", is_mystery=True)], carrier=1))
        for base, loop, waves, loop_waves in itertools.product([True, False], repeat=4):
            samples.append(sample(f"clear-order-{len(samples)}", clear=True, poor=True,
                                  tiles="ME MB MM MS\nSP -- -- --\n-- -- -- --" if base else "-- -- -- --\n-- -- -- --\n-- -- -- --",
                                  loop="ME MB MM MS\nSP -- -- --\n-- -- -- --" if loop else "-- -- -- --\n-- -- -- --\n-- -- -- --",
                                  waves=[dict(battle=0, enemy=1)] if waves else [], loop_waves=[dict(battle=0, enemy=1)] if loop_waves else []))
        for mode in ["normal", "init", "carrier", "movable", "decoy"]:
            for count in [0, 1, 2, 3, 9]:
                samples.append(sample(f"row-index-{mode}-{count}", mode=mode, battle=count, mystery=1, siren=1, carrier=2,
                                      patches=[patch("A1", is_enemy=True), patch("B2", is_enemy=True)]))
        samples.extend([
            sample("all-inferred", waves=[dict(battle=0, enemy=1, mystery=1, siren=1, boss=1)]),
            sample("already-observed", waves=[dict(battle=0, enemy=1, mystery=1, siren=1, boss=1)],
                   patches=[patch("A1", is_enemy=True), patch("B1", is_boss=True), patch("C1", is_mystery=True), patch("D1", is_siren=True)]),
            sample("ambiguous-enemy", tiles="ME ME MM MS\nSP -- -- --\n-- -- -- --", waves=[dict(battle=0, enemy=1)]),
            sample("carrier-only", covered=["A2", "B2"], waves=[dict(battle=0)], mode="carrier", carrier=2),
            sample("boss-before-carrier", covered=["B1"], waves=[dict(battle=0, boss=1)], mode="carrier", carrier=1),
            sample("overlapping-occlusion", covered=["A1", "A1", "A2"], waves=[dict(battle=0, enemy=1)],
                   patches=[patch("A2", is_fleet=True), patch("A3", is_current_fleet=True, is_fleet=True)]),
            sample("cleared-mechanisms", fortress=True, bouncing=True, fortress_enemies=["C2"], bouncing_routes=[["B2", "B3"]],
                   patches=[patch("C2", is_fortress=False), patch("B2", may_bouncing_enemy=False), patch("B3", may_bouncing_enemy=False)]),
            sample("append-stack", reload=[True, False]),
            sample("empty-stack", waves=[], loop_waves=[]),
            sample("poor-empty-stack", waves=[], loop_waves=[], poor=True),
            sample("reset-observed", before=[patch("A1", is_enemy=True), patch("B2", is_fleet=True, is_current_fleet=True)])])
        rng = random.Random(172405)
        for index in range(600):
            tokens = [rng.choice(["ME", "Me", "MB", "MM", "MS", "MA", "SP", "__", "++", "--"]) for _ in range(12)]
            patches = []
            for y in range(3):
                for x in range(4):
                    if rng.randrange(3) == 0:
                        patches.append(patch(location2node((x, y)), **{k: bool(rng.randrange(2)) for k in
                            ["is_enemy", "is_boss", "is_siren", "is_mystery", "is_fleet", "is_current_fleet", "is_fortress", "may_bouncing_enemy"]}))
            samples.append(sample(f"random-{index}", tiles="\n".join(" ".join(tokens[y * 4:y * 4 + 4]) for y in range(3)),
                                  clear=bool(index & 1), poor=bool(index & 2), mode=rng.choice(["normal", "init", "carrier", "movable", "decoy"]),
                                  battle=rng.randrange(6), mystery=rng.randrange(3), siren=rng.randrange(4), carrier=rng.randrange(4),
                                  fortress=bool(index & 4), bouncing=bool(index & 8), fortress_enemies=["C2"], fortress_blocks=["D2"],
                                  bouncing_routes=[["B2", "B3"]], patches=patches,
                                  waves=[dict(battle=i * 2, enemy=rng.randrange(3), mystery=rng.randrange(2), siren=rng.randrange(2), boss=rng.randrange(2)) for i in range(4)]))

        def snapshot(mapping):
            return [{k: bool(getattr(g, k)) for k in fields} for g in mapping]

        def waves(rows):
            return [dict(battle=row['battle'], **{k: row.get(k, 0) for k in ["enemy", "mystery", "siren", "boss"]}) for row in rows]

        results = []
        for item in samples:
            mapping = CampaignMap("reference")
            mapping.shape, mapping.map_data = item["shape"], item["tiles"]
            mapping.map_data_loop = item["loop"]
            mapping.spawn_data, mapping.spawn_data_loop = item["waves"], item["loop_waves"]
            mapping.map_covered = item["covered"]
            mapping.fortress_data = [item["fortress_enemies"], item["fortress_blocks"]]
            mapping.bouncing_enemy_data = item["bouncing_routes"]
            for p in item["before"]:
                for key, value in p["state"].items():
                    setattr(mapping[node2location(p["cell"])], key, value)
            fleet = object.__new__(Fleet)
            fleet.config = SimpleNamespace(POOR_MAP_DATA=item["poor"], MAP_HAS_WALL=False, MAP_HAS_PORTAL=False,
                                           MAP_HAS_LAND_BASED=False, MAP_HAS_MAZE=False, MAP_HAS_FORTRESS=item["fortress"],
                                           MAP_HAS_BOUNCING_ENEMY=item["bouncing"])
            fleet.map_is_clear_mode = item["clear"]
            fleet.battle_count = fleet.mystery_count = fleet.carrier_count = fleet.siren_count = 9
            fleet.fleet_1_location = fleet.fleet_2_location = fleet.fleet_submarine_location = (1, 1)
            fleet.fleet_current_index = 2
            # Invoke native methods without constructors/devices or replacement business logic.
            Fleet.map_data_init(fleet, mapping)
            initialized = dict(cells=snapshot(mapping), poor=mapping.poor_map_data,
                               complete=mapping.is_map_data_poor, stack=copy.deepcopy(mapping.spawn_data_stack),
                               active=waves(mapping.spawn_data), progress=[fleet.battle_count, fleet.mystery_count, fleet.siren_count, fleet.carrier_count],
                               fleets=[list(fleet.fleet_1_location), list(fleet.fleet_2_location), list(fleet.fleet_submarine_location)],
                               fleet_index=fleet.fleet_current_index, ammo=fleet.ammo_count,
                               fortress=[len(g) for g in mapping.fortress_data], bouncing=len(mapping.bouncing_enemy_data))
            for loop in item["reload"]:
                mapping.load_spawn_data(use_loop=loop)
            for p in item["patches"]:
                for key, value in p["state"].items():
                    setattr(mapping[node2location(p["cell"])], key, value)
            arguments = dict(battle_count=item["battle"], mystery_count=item["mystery"], siren_count=item["siren"], carrier_count=item["carrier"], mode=item["mode"])
            covered = sorted(location2node(g.location) for g in mapping.map_covered)
            try:
                may, missing = mapping.missing_get(**arguments)
                census = dict(may=may, missing=missing)
            except IndexError:
                census = None
            try:
                none = mapping.missing_is_none(**arguments)
                before = snapshot(mapping)
                mapping.missing_predict(**arguments)
                after = snapshot(mapping)
                changed = sorted(location2node(g.location) for g, old, new in zip(mapping, before, after) if old != new)
                error = None
            except IndexError:
                none, changed, after, error = None, [], snapshot(mapping), "empty-spawn-table"
            results.append(dict(sample=item, initialized=initialized, stack=copy.deepcopy(mapping.spawn_data_stack), active=waves(mapping.spawn_data),
                                covered=covered, census=census, none=none, changed=changed, after=after, error=error))
    output.write_text(json.dumps(results, separators=(",", ":")), encoding="utf-8")


if __name__ == "__main__":
    main()
