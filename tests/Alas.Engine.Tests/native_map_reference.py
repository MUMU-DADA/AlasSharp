"""Serialize the live upstream CampaignMap declarations for the C# map audit.

This is deliberately independent of the generator and its static resolver.
Successful imports use the complete native module. Failed imports are recorded;
only their MAP dependency slice is executed, without replacing missing assets.
That narrower check does not certify the Campaign or its entrance.
"""
from __future__ import annotations

import contextlib
import ast
import copy
import importlib
import itertools
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


FLAGS = ("is_land", "is_spawn_point", "is_submarine_spawn_point", "may_enemy",
         "may_boss", "may_mystery", "may_ammo", "may_siren", "may_ambush")


def node(value):
    from module.base.utils import location2node
    if isinstance(value, str):
        return value
    if isinstance(value, tuple):
        return location2node(value)
    return str(value)


def cells(value):
    return [node(item) for item in value]


def waves(value):
    return [
        {key: int(item.get(key, 0)) for key in ("battle", "enemy", "mystery", "boss", "siren")}
        for item in (value or [])
    ]


def groups(value):
    return [cells(group) for group in (value or [])]


def flags(value):
    return [[bool(getattr(grid, field)) for field in FLAGS] for grid in value]


def declaration_slice(path, module_name):
    """Execute only top-level MAP mutations and their named dependencies.

    This is an independent Python AST slice, not the generator's MAP evaluator.
    Native constructors, setters, references and calls execute unchanged.
    Missing dependencies/control flow fail instead of being stubbed.
    """
    tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
    selected, needed = [], {"MAP"}
    for item in reversed(tree.body):
        stores = {n.id for n in ast.walk(item) if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Store)}
        touches_map = any(isinstance(n, ast.Name) and n.id == "MAP" for n in ast.walk(item))
        if isinstance(item, (ast.Assign, ast.AnnAssign)):
            take = bool(stores & needed) or touches_map
        elif isinstance(item, ast.Expr):
            take = touches_map
        elif isinstance(item, (ast.Import, ast.ImportFrom)):
            aliases = [a for a in item.names if (a.asname or a.name.split(".")[0]) in needed]
            take = bool(aliases)
            item.names = aliases
        elif isinstance(item, (ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            take = item.name in needed
        elif touches_map:
            raise ValueError("unsupported MAP control flow in native declaration slice")
        else:
            take = False
        if take:
            selected.append(item)
            needed.difference_update(stores)
            needed.update(n.id for n in ast.walk(item) if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Load))
    scope = {"__name__": module_name, "__package__": module_name.rsplit(".", 1)[0]}
    exec(compile(ast.Module(body=list(reversed(selected)), type_ignores=[]), str(path), "exec"), scope)
    return scope["MAP"]


def map_record(source, campaign_map):
    from module.map_detection.grid_info import GridInfo
    shape = campaign_map.shape
    weights = [float(grid.weight) for grid in campaign_map]
    fortress = campaign_map._fortress_data or ((), ())
    loop = copy.deepcopy(campaign_map)
    loop.load_map_data(use_loop=True)
    # Compare wall topology via the native graph, not a second wall parser.
    topology = copy.deepcopy(campaign_map)
    topology.grid_connection_initial(wall=True, portal=True)
    probes = []
    for globe, _ in campaign_map._ignore_prediction:
        for scale, genre, siren in itertools.product(range(4), (None, "", "Enemy", "Light"), (False, True)):
            info = GridInfo()
            info.enemy_scale, info.enemy_genre, info.is_siren = scale, genre, siren
            probes.append(dict(cell=node(globe), info=dict(enemy_scale=scale, enemy_genre=genre, is_siren=siren),
                               matches=bool(campaign_map.ignore_prediction_match(globe, info))))
    return {
        "name": campaign_map.name,
        "shape": [int(shape[0]) + 1, int(shape[1]) + 1],
        "flags": flags(campaign_map),
        "loop_flags": flags(loop),
        "has_loop": bool(campaign_map.map_data_loop),
        "weights": weights,
        "cameras": cells(campaign_map.camera_data),
        "spawn_cameras": cells(campaign_map.camera_data_spawn_point),
        "waves": waves(campaign_map._spawn_data),
        "loop_waves": waves(campaign_map._spawn_data_loop),
        "covered": cells(campaign_map._map_covered),
        "camera_sight": [int(item) for item in campaign_map.camera_sight],
        "swipe_preset": None if campaign_map.in_map_swipe_preset_data is None else [
            int(item) for item in campaign_map.in_map_swipe_preset_data
        ],
        "connections": {node(k): sorted(node(v) for v in values) for k, values in topology.grid_connection.items()},
        "portals": [[node(a), node(b)] for a, b in campaign_map._portal_data],
        "land_based": [[node(a), str(b)] for a, b in campaign_map._land_based_data],
        "mazes": groups(campaign_map._maze_data),
        "fortress": [cells(fortress[0]), cells(fortress[1])],
        "bouncing": groups(campaign_map._bouncing_enemy_data),
        "ignored": probes,
        "ignored_count": len(campaign_map._ignore_prediction),
        "grid_class": campaign_map.grid_class.__module__ + "." + campaign_map.grid_class.__name__,
        "source": source,
    }


def custom_grid_checks():
    from campaign.campaign_main.campaign_15_base import W15GridInfo
    from module.map.map_base import CampaignMap
    from module.map_detection.grid_info import GridInfo
    fields = ("is_submarine", "is_caught_by_siren", "is_fleet", "is_current_fleet", "is_boss",
              "is_siren", "is_enemy", "enemy_scale", "enemy_genre")
    results = []
    for land, siren, boss, fleet, submarine, caught, conflicts, mode in itertools.product(
            (False, True), (False, True), (False, True), (False, True), (False, True), (False, True),
            range(3), ("normal", "init", "carrier", "movable", "decoy")):
        before = dict(is_land=land, may_siren=siren, is_submarine_spawn_point=True,
                      enemy_scale=3, enemy_genre="Main")
        observed = dict(is_boss=boss, is_fleet=fleet, is_submarine=submarine,
                        is_caught_by_siren=caught, is_current_fleet=True)
        mapping = CampaignMap()
        mapping.grid_class = W15GridInfo
        mapping.shape = "C1"
        mapping.map_data = "MS ++ ++"
        target, info = mapping[(0, 0)], GridInfo()
        for key, value in before.items():
            setattr(target, key, value)
        for key, value in observed.items():
            setattr(info, key, value)
        info.location = (0, 0)
        direct = copy.copy(target)
        merged = direct.merge(info, mode=mode)
        grids = {(0, 0): info}
        for x in range(1, conflicts + 1):
            bad = GridInfo()
            bad.location, bad.is_boss = (x, 0), True
            grids[(x, 0)] = bad
        accepted = mapping.update(SimpleNamespace(grids=grids, center_loca=(0, 0)), (0, 0), mode=mode)
        results.append(dict(before=before, info=observed, conflicts=conflicts, mode=mode,
                            merged=merged, direct={k: getattr(direct, k) for k in fields},
                            accepted=accepted, frame={k: getattr(target, k) for k in fields}))
    return results


def main() -> int:
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    # Keep native logger artifacts under the ignored verification directory.
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w", encoding="utf-8") as quiet, contextlib.redirect_stdout(quiet):
        from module.logger import logger
        logger.setLevel(50)
        from module.map.map_base import CampaignMap
        records = {}
        import_failures = []
        support_modules = []
        for path in sorted((root / "campaign").rglob("*.py")):
            source = path.relative_to(root).as_posix()
            module_name = source[:-3].replace("/", ".")
            try:
                module = importlib.import_module(module_name)
                campaign_map = getattr(module, "MAP", None)
                scope = "full_module"
            except ImportError as error:
                import_failures.append({"source": source, "error": f"{type(error).__name__}: {error}"})
                campaign_map = declaration_slice(path, module_name)
                scope = "declaration_only"
            if isinstance(campaign_map, CampaignMap):
                record = map_record(source, campaign_map)
                record["scope"] = scope
                records[source.removeprefix("campaign/").removesuffix(".py")] = record
            else:
                support_modules.append(source)
        custom = custom_grid_checks()
    output.write_text(json.dumps({"records": records, "import_failures": import_failures,
                                 "support_modules": support_modules, "custom_grid": custom}, ensure_ascii=False,
                                 separators=(",", ":")), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
