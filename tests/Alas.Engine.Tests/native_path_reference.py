"""Exercise native map methods; independent Bellman-Ford verifies weighted convergence."""
import ast
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
        from module.base.utils import location2node, node2location
        from module.map.map_base import CampaignMap
        from module.logger import logger
        logger.setLevel("CRITICAL")

        def wall_text(width, height, walls):
            lines = [[" "] * (4 * width - 1) for _ in range(2 * height - 1)]
            # Upstream wall data uses markers, four-character indentation, and two trailing columns.
            lines[0][0] = "+"
            for start, end in walls:
                x, y = node2location(start)
                a, b = node2location(end)
                lines[2 * min(y, b) + int(y != b)][4 * min(x, a) + 2 * int(x != a)] = "|" if x != a else "-"
            return "\n".join("    " + "".join(line) + "  " for line in lines)

        fields = ["is_land", "may_ambush", "is_enemy", "is_siren", "is_fortress", "is_boss", "is_fleet",
                  "is_mechanism_trigger", "is_mechanism_block", "is_portal", "is_maze", "is_flare", "may_bouncing_enemy", "weight"]
        def setup(sample):
            mapping = CampaignMap("offline")
            mapping.shape, mapping.map_data = sample["shape"], sample["tiles"]
            mapping.map_data_loop = sample.get("loop", "")
            mapping.load_map_data(sample.get("useLoop", False))
            if "weights" in sample:
                for grid, weight in zip(mapping, sample["weights"]):
                    grid.weight = weight
            width, height = mapping.shape[0] + 1, mapping.shape[1] + 1
            mapping.wall_data = wall_text(width, height, sample["walls"])
            mapping.portal_data = sample["portals"]
            mapping.grid_connection_initial(wall=sample["enableWalls"], portal=sample["enablePortals"])
            for patch in sample["cells"]:
                grid = mapping[tuple(node2location(patch["cell"]))]
                for key, value in patch["values"].items():
                    setattr(grid, key, value)
            mechanism = sample["mechanisms"]
            mapping.land_based_data = [(m["origin"], m["direction"]) for m in mechanism["land"]]
            mapping.maze_data = mechanism["mazes"]
            mapping.fortress_data = [mechanism["fortressEnemies"], mechanism["fortressBlocks"]]
            mapping.bouncing_enemy_data = mechanism["bouncing"]
            mapping.load_mechanism(**sample["enableMechanisms"])
            return mapping

        def snapshot(mapping):
            def group(value):
                return None if value is None else sorted(location2node(g.location) for g in value)
            return [dict(cell=location2node(g.location), values={k: getattr(g, k) for k in fields},
                         portal=None if not g.portal_link else location2node(g.portal_link),
                         rounds=list(g.maze_round), nearby=group(g.maze_nearby),
                         triggers=group(g.mechanism_trigger), blocks=group(g.mechanism_block)) for g in mapping]

        def costs(mapping):
            return [dict(cell=location2node(g.location), cost=g.cost, cost1=g.cost_1, cost2=g.cost_2,
                         previous=None if g.connection is None else location2node(g.connection)) for g in mapping]

        def shortest(mapping, start, ambush, enemy):
            start = tuple(node2location(start))
            distances = dict.fromkeys(mapping.grids, 9999)
            distances[start] = 0
            for _ in range(len(distances)):
                improved = False
                # Different algorithm and iteration than the product's priority queue.
                for point, grid in mapping.grids.items():
                    if point != start and enemy and not grid.is_sea:
                        continue
                    for dest in mapping.grid_connection[point]:
                        target = mapping[dest]
                        if target.is_land or target.is_mechanism_block:
                            continue
                        cost = distances[point] + (10 if ambush and target.may_ambush else 1)
                        if cost < distances[dest]:
                            distances[dest] = cost
                            improved = True
                if not improved:
                    break
            else:
                raise AssertionError("Positive-cost oracle did not converge")
            return {location2node(k): v for k, v in distances.items()}

        def empty_mechanisms():
            return dict(land=[], mazes=[], fortressEnemies=[], fortressBlocks=[], bouncing=[])
        def sample(name, shape, tiles, **extra):
            result = dict(name=name, shape=shape, tiles=tiles, walls=[], portals=[], enableWalls=False, enablePortals=False,
                          cells=[], mechanisms=empty_mechanisms(), enableMechanisms=dict(land_based=False, maze=False, fortress=False, bouncing_enemy=False),
                          start="A1", second=shape, hasAmbush=False, hasEnemy=True)
            result.update(extra)
            return result

        samples = []
        # Source literals are reference input only, not executable plans and not product resources.
        for file in sorted((root / "campaign/campaign_main").glob("campaign_1_*.py")) + [root / "campaign/campaign_main/campaign_13_2.py"]:
            attributes = {}
            for node in ast.parse(file.read_text(encoding="utf-8")).body:
                if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Attribute) and isinstance(node.targets[0].value, ast.Name) and node.targets[0].value.id == "MAP":
                    try:
                        attributes[node.targets[0].attr] = ast.literal_eval(node.value)
                    except (TypeError, ValueError):
                        pass
            for ambush, enemy in itertools.product([False, True], repeat=2):
                weights = [float(w) for w in attributes.get("weight_data", "").split()]
                item = sample(file.stem, attributes["shape"], attributes["map_data"], hasAmbush=ambush, hasEnemy=enemy)
                if weights:
                    item["weights"] = weights
                # Spawn points are declared, not guessed from chapter identity.
                rows = [row.split() for row in attributes["map_data"].strip().splitlines()]
                item["start"] = next(location2node((x, y)) for y, row in enumerate(rows) for x, token in enumerate(row) if token == "SP")
                samples.append(item)
        rng = random.Random(675312)
        for i in range(240):
            width, height = rng.randrange(3, 10), rng.randrange(2, 8)
            cells = [location2node((x, y)) for y in range(height) for x in range(width)]
            tiles = [rng.choice(["--", "--", "--", "ME", "Me", "MB", "MM", "MA", "MS", "++", "SP", "__"]) for _ in cells]
            tiles[0] = tiles[-1] = "SP"
            walls = []
            for y in range(height):
                for x in range(width):
                    for a, b in [(x + 1, y), (x, y + 1)]:
                        if a < width and b < height and rng.randrange(12) == 0:
                            walls.append([location2node((x, y)), location2node((a, b))])
            portals = [[cells[width], cells[-1]], [cells[1], cells[2]]] if i % 3 == 0 else []
            patches = []
            for name in cells[1:-1]:
                if rng.randrange(4) == 0:
                    patches.append(dict(cell=name, values={rng.choice(["is_enemy", "is_siren", "is_boss", "is_fortress", "is_mechanism_block", "is_fleet", "is_flare"]): True}))
            samples.append(sample(f"synthetic-{i}", cells[-1], "\n".join(" ".join(tiles[y * width:(y + 1) * width]) for y in range(height)),
                                  walls=walls, portals=portals, enableWalls=bool(i & 1), enablePortals=bool(i & 2), cells=patches,
                                  hasAmbush=bool(i & 4), hasEnemy=bool(i & 8), weights=[rng.randrange(1, 100) / 2 for _ in cells]))
        for index, flags in enumerate(itertools.product([False, True], repeat=6)):
            samples.append(sample(f"mechanisms-{index}", "G5", "\n".join(["-- ME -- -- -- -- --", "-- -- ++ -- -- -- --", "-- -- -- -- -- -- --", "-- -- -- -- -- -- --", "-- -- -- -- -- -- --"]),
                                  loop="\n".join(["-- -- -- -- -- -- --"] * 5), useLoop=bool(index & 1),
                                  walls=[["D2", "D3"], ["A4", "B4"]], portals=[["F4", "A5"], ["A2", "A3"]], enableWalls=flags[0], enablePortals=flags[1],
                                  mechanisms=dict(land=[dict(origin="C2", direction="right"), dict(origin="G5", direction="up")],
                                                  mazes=[["B2", "E3"], ["E4"]], fortressEnemies=["F2", "F3"], fortressBlocks=["G3"], bouncing=[["A4", "B4", "C4"]]),
                                  enableMechanisms=dict(zip(["land_based", "maze", "fortress", "bouncing_enemy"], flags[2:]))))
        samples.append(sample("long-route", "BZ1", " ".join(["--"] * 78)))
        results = []
        for item in samples:
            mapping = setup(item)
            declaration = snapshot(mapping)
            links = {location2node(k): sorted(location2node(v) for v in values) for k, values in mapping.grid_connection.items()}
            optimum = shortest(mapping, item["start"], item["hasAmbush"], item["hasEnemy"])
            mapping.find_path_initial(item["start"], has_ambush=item["hasAmbush"], has_enemy=item["hasEnemy"])
            native = costs(mapping)
            routes = []
            destinations = list(mapping.grids)[::max(1, len(mapping.grids) // 7)]
            if list(mapping.grids)[-1] not in destinations:
                destinations.append(list(mapping.grids)[-1])
            for dest in destinations:
                full = mapping._find_path(dest)
                for step, turning in itertools.product([0, 1, 2, 3, 5], [False, True]):
                    nodes = mapping.find_path(dest, step=step, turning_optimize=turning)
                    routes.append(dict(destination=location2node(dest), full=None if full is None else [location2node(p) for p in full],
                                       step=step, turning=turning, nodes=[location2node(p) for p in nodes]))
            mapping.find_path_initial_multi_fleet({1: tuple(node2location(item["start"])), 2: tuple(node2location(item["second"]))},
                                                 tuple(node2location(item["start"])), item["hasAmbush"])
            multi = costs(mapping)
            optimum_multi = [shortest(mapping, item[k], item["hasAmbush"], True) for k in ["start", "second"]]
            trigger = next((g for g in mapping if g.is_mechanism_trigger), None)
            if trigger is not None:
                trigger.wipe_out()
            wiped = snapshot(mapping)
            mapping.reset()
            results.append(dict(sample=item, declaration=declaration, links=links, optimum=optimum, native=native,
                                routes=routes, multi=multi, optimumMulti=optimum_multi,
                                mazeRound=mapping.maze_round, trigger=None if trigger is None else location2node(trigger.location),
                                wiped=wiped, reset=snapshot(mapping)))
    output.write_text(json.dumps(results, separators=(",", ":")), encoding="utf-8")


if __name__ == "__main__":
    main()
