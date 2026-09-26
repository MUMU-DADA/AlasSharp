"""Generate compiled C# map declarations from the pinned upstream campaign snapshot.

This is a build-time source generator. The product never reads campaign JSON and
never interprets a plan. Unsupported map mechanisms fail generation instead of
silently becoming a default map.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import math
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from upstream_map_export import MapResolver

FIELDS = {"shape", "map_data", "map_data_loop", "weight_data", "spawn_data", "spawn_data_loop",
          "camera_data", "camera_data_spawn_point", "camera_sight", "in_map_swipe_preset_data",
          "grid_class", "name", "wall_data", "portal_data", "land_based_data", "maze_data",
          "fortress_data", "bouncing_enemy_data", "map_covered"}
GRID_TYPES = {"campaign.campaign_main.campaign_15_base.W15GridInfo": (
    "Main.W15CellState", "60f28ffa4c3c6a8255428d99e974f8d7ef7f1c4ea8f3921d4ae5fc48bb2e4ed6")}


def literal(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def source_hash(root: Path, relative: str) -> str:
    return hashlib.sha256((root / relative).read_bytes()).hexdigest()


def parse_weights(value: object, columns: int, rows: int) -> list[str] | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise ValueError("weight_data must be text")
    parsed = map_rows(value, "weight_data")
    if len(parsed) != rows or len(parsed[0]) != columns:
        raise ValueError("weight_data shape does not match map")
    raw = [token for row in parsed for token in row]
    weights: list[str] = []
    for token in raw:
        try:
            number = float(token)
        except ValueError as error:
            raise ValueError(f"invalid weight_data value {token!r}") from error
        if not math.isfinite(number):
            raise ValueError(f"non-finite weight_data value {token!r}")
        weights.append(repr(int(number)) if number.is_integer() else repr(number))
    return weights


def spawn_waves(value: object, source: str) -> str:
    if not isinstance(value, list):
        raise ValueError(f"spawn_data must be a list in {source}")
    result = []
    for wave in value:
        if (not isinstance(wave, dict) or type(wave.get("battle")) is not int
                or set(wave) - {"battle", "enemy", "mystery", "boss", "siren"}
                or any(type(n) is not int or n < 0 for n in wave.values())):
            raise ValueError(f"invalid spawn wave in {source}")
        values = [str(wave["battle"])] + [str(int(wave.get(k, 0))) for k in ("enemy", "mystery", "boss", "siren")]
        result.append(f"new({', '.join(values)})")
    return "[" + ", ".join(result) + "]"


def wall_edges(value: object, source: str) -> list[str]:
    if value is None or value == "":
        return []
    if not isinstance(value, str):
        raise ValueError(f"wall_data must be text in {source}")
    marks = []
    for y, line in enumerate(line for line in value.splitlines() if line):
        for x, char in enumerate(line[4:-2]):
            if char != " ": marks.append((x, y))
    edges = []
    for x, y in marks:
        if x % 4 == 2 and y % 2 == 0:
            a = (x - 2) // 4 + 1, y // 2 + 1
            edges.append((a, (a[0] + 1, a[1])))
        elif x % 4 == 0 and y % 2 == 1:
            a = x // 4 + 1, (y - 1) // 2 + 1
            edges.append((a, (a[0], a[1] + 1)))
    return [f'new MapEdge("{column_label(a[0])}{a[1]}", "{column_label(b[0])}{b[1]}")' for a, b in edges]


def cell_list(value: object) -> list[str]:
    if value is None: return []
    if isinstance(value, str): return [value]
    if isinstance(value, list) and all(isinstance(item, str) for item in value): return value
    raise ValueError(f"invalid cell list: {value!r}")


def cell_expr(value: object, source: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[A-Z]+[1-9][0-9]*", value):
        raise ValueError(f"invalid map cell in {source}: {value!r}")
    return f'Cell.Parse("{value}")'


def cell_group_expr(value: object, source: str) -> str:
    cells = cell_list(value)
    return "[" + ", ".join(cell_expr(cell, source) for cell in cells) + "]"


def column_label(number: int) -> str:
    result = ""
    while number:
        number, remainder = divmod(number - 1, 26)
        result = chr(65 + remainder) + result
    return result


def column_width(label: str) -> int:
    number = 0
    for char in label:
        number = number * 26 + ord(char) - 64
    return number


def mechanism_expr(mapping: dict, source: str) -> str | None:
    args: list[str] = []
    maze = mapping.get("maze_data")
    if maze is not None:
        if not isinstance(maze, list):
            raise ValueError(f"maze_data must be a list in {source}")
        args.append("mazes: [" + ", ".join(cell_group_expr(group, source) for group in maze) + "]")
    fortress = mapping.get("fortress_data")
    if fortress is not None:
        if not isinstance(fortress, list) or len(fortress) != 2:
            raise ValueError(f"fortress_data must contain enemy and block groups in {source}")
        args.extend(["fortressEnemies: " + cell_group_expr(fortress[0], source),
                     "fortressBlocks: " + cell_group_expr(fortress[1], source)])
    bouncing = mapping.get("bouncing_enemy_data")
    if bouncing is not None:
        if not isinstance(bouncing, list):
            raise ValueError(f"bouncing_enemy_data must be a list in {source}")
        args.append("bouncingRoutes: [" + ", ".join(cell_group_expr(group, source) for group in bouncing) + "]")
    land = mapping.get("land_based_data")
    if land is not None:
        if not isinstance(land, list):
            raise ValueError(f"land_based_data must be a list in {source}")
        declarations = []
        for item in land:
            if not isinstance(item, list) or len(item) != 2 or item[1] not in ("up", "down", "left", "right"):
                raise ValueError(f"invalid land_based_data item in {source}: {item!r}")
            declarations.append(f'new LandMechanism({cell_expr(item[0], source)}, MapDirection.{str(item[1]).title()})')
        args.append("landBased: [" + ", ".join(declarations) + "]")
    return "new MapMechanisms(" + ", ".join(args) + ")" if args else None


def ignored_prediction_expr(calls: list[dict], source: str) -> str | None:
    predicates: list[str] = []
    names = {"enemy_scale": "EnemyScale", "enemy_genre": "EnemyGenre", "is_siren": "IsSiren"}
    types = {"enemy_scale": int, "enemy_genre": str, "is_siren": bool}
    for call in calls:
        if call.get("method") != "ignore_prediction" or len(call.get("args", [])) != 1:
            raise ValueError(f"unsupported MAP call in {source}: {call!r}")
        cell = cell_expr(call["args"][0], source)
        checks = []
        for name, value in call.get("kwargs", {}).items():
            if name not in names or type(value) is not types[name]:
                raise ValueError(f"unsupported ignore_prediction field in {source}: {name}")
            if isinstance(value, bool):
                expected = "true" if value else "false"
            elif isinstance(value, int):
                expected = str(value)
            else:
                expected = json.dumps(value, ensure_ascii=False)
            checks.append(f"observation.{names[name]} == {expected}")
        if not checks:
            raise ValueError(f"empty ignore_prediction predicate in {source}")
        predicates.append(f"new IgnoredPrediction({cell}, observation => { ' && '.join(checks) })")
    return "[" + ", ".join(predicates) + "]" if predicates else None


def validate_sight(sight: object) -> None:
    if (not isinstance(sight, list) or len(sight) != 4 or any(type(item) is not int for item in sight)
            or not sight[0] <= 0 <= sight[2] or not sight[1] <= 0 <= sight[3]):
        raise ValueError("camera_sight requires four integer bounds enclosing the center")


def default_cameras(columns: int, rows: int, sight: list[int]) -> list[str]:
    # Native shape setter's camera_2d. Assignment order determines its sight.
    validate_sight(sight)
    def axis(size, left, right):
        if size <= abs(left):
            return [size // 2]
        return [n for n in set([*range(abs(left), 26, right - left + 1), size - right]) if n <= size - right]
    return [column_label(x + 1) + str(y + 1) for x in axis(columns - 1, sight[0], sight[2])
            for y in axis(rows - 1, sight[1], sight[3])]


def validate_assignment_order(root: Path, source: str, resolved: dict) -> None:
    """Only collapse setters when the declaration model preserves their effects.

    Native shape resets grids/cameras/weights, and some setters append rather
    than replace. A last-value summary cannot represent arbitrary overwrites.
    Refuse those sources pending an explicit semantic migration.
    """
    origins = {k: v for k, v in resolved["origins"].items() if k != "name"}
    modules = {origin["module"] for origin in origins.values()}
    if len(modules) != 1:
        raise ValueError(f"cross-module MAP setter order needs explicit migration: {source}")
    for module in modules:
        tree = ast.parse((root / (module.replace(".", "/") + ".py")).read_text(encoding="utf-8-sig"))
        assigned: set[str] = set()
        for item in tree.body:
            if not isinstance(item, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
                continue
            targets = item.targets if isinstance(item, ast.Assign) else [item.target]
            for target in targets:
                if isinstance(target, ast.Attribute) and isinstance(target.value, ast.Name) and target.value.id == "MAP":
                    if target.attr in assigned or isinstance(item, ast.AugAssign):
                        raise ValueError(f"repeated MAP setter needs explicit migration: {source}: {target.attr}")
                    assigned.add(target.attr)
    initialized = origins.get("shape", origins["map_data"])["line"]
    after = ("map_data", "weight_data", "camera_data", "camera_data_spawn_point", "portal_data",
             "fortress_data", "bouncing_enemy_data", "map_covered")
    if any(name in origins and origins[name]["line"] < initialized for name in after):
        raise ValueError(f"MAP setter precedes grid initialization: {source}")
    if "grid_class" in origins and origins["grid_class"]["line"] >= initialized:
        raise ValueError(f"grid_class assigned after grid initialization: {source}")


def map_rows(value: object, source: str) -> list[list[str]]:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"missing map_data in {source}")
    # CampaignMap._parse_text splits on one literal space, not arbitrary
    # whitespace. Reject unsupported layout instead of normalizing its meaning.
    rows = [line.strip().split(" ") for line in value.strip().split("\n")]
    if any(not token or any(char.isspace() for char in token) for row in rows for token in row):
        raise ValueError(f"unsupported map_data whitespace in {source}")
    if any(len(row) != len(rows[0]) for row in rows):
        raise ValueError(f"ragged map in {source}")
    return rows


def map_entry(root: Path, source: str, resolved: dict) -> str:
    mapping, calls = resolved["values"], resolved["calls"]
    if set(mapping) - FIELDS:
        raise ValueError(f"unsupported MAP fields in {source}: {sorted(set(mapping) - FIELDS)}")
    if any(value is None and name not in ("name", "in_map_swipe_preset_data") for name, value in mapping.items()):
        raise ValueError(f"unsupported explicit null MAP field in {source}")
    if resolved["name"] is not None and not isinstance(resolved["name"], str):
        raise ValueError(f"MAP name must be text or null in {source}")
    if "name" in mapping and mapping["name"] != resolved["name"]:
        raise ValueError(f"MAP name declaration differs in {source}")
    parsed_rows = map_rows(mapping.get("map_data"), source)
    rows, columns = len(parsed_rows), len(parsed_rows[0])
    validate_assignment_order(root, source, resolved)
    declared_shape = mapping.get("shape")
    if declared_shape is None:
        shape = column_label(columns) + str(rows)
    else:
        if not isinstance(declared_shape, str):
            raise ValueError(f"invalid shape in {source}: {declared_shape!r}")
        shape = declared_shape.upper()
        match = re.fullmatch(r"([A-Z]+)([1-9][0-9]*)", shape)
        if not match or int(match.group(2)) != rows or column_width(match.group(1)) != columns:
            raise ValueError(f"shape does not match map data in {source}: {shape!r}")
    weights = parse_weights(mapping.get("weight_data"), columns, rows)
    cameras = mapping.get("camera_data")
    if cameras is None:
        sight_origin = resolved["origins"].get("camera_sight")
        shape_origin = resolved["origins"].get("shape", resolved["origins"]["map_data"])
        sight = [-3, -1, 3, 2]
        if sight_origin:
            if sight_origin["module"] != shape_origin["module"]:
                raise ValueError(f"camera initialization order needs explicit migration: {source}")
            if sight_origin["line"] < shape_origin["line"]:
                sight = mapping["camera_sight"]
        cameras = default_cameras(columns, rows, sight)
    spawn_cameras = mapping.get("camera_data_spawn_point", [])
    spawn_data = mapping.get("spawn_data", [])
    if not isinstance(cameras, list) or not isinstance(spawn_cameras, list) or not isinstance(spawn_data, list):
        raise ValueError(f"invalid camera/spawn declaration in {source}")
    for camera in cameras + spawn_cameras:
        cell_expr(camera, source)
    wave_text = spawn_waves(spawn_data, source)
    key = source.removeprefix("campaign/").removesuffix(".py").replace("\\", "/")
    args = [literal(shape), literal(mapping["map_data"]),
            "[" + ", ".join(literal(x) for x in cameras) + "]",
            "[" + ", ".join(literal(x) for x in spawn_cameras) + "]",
            wave_text]
    if "map_data_loop" in mapping and mapping["map_data_loop"] != "":
        loop_rows = map_rows(mapping["map_data_loop"], source)
        if len(loop_rows) != rows or len(loop_rows[0]) != columns:
            raise ValueError(f"loop map shape differs in {source}")
        args.append("loopTiles: " + literal(mapping["map_data_loop"]))
    if weights is not None:
        args.append("weights: [" + ", ".join(weights) + "]")
    loop_waves = mapping.get("spawn_data_loop")
    if loop_waves is not None:
        args.append("loopWaves: " + spawn_waves(loop_waves, source))
    covered = cell_list(mapping.get("map_covered"))
    if covered:
        args.append("covered: [" + ", ".join(literal(x) for x in covered) + "]")
    walls = wall_edges(mapping.get("wall_data"), source)
    portals = mapping.get("portal_data", [])
    if not isinstance(portals, list):
        raise ValueError(f"portal_data must be a list in {source}")
    portal_edges = []
    for pair in portals:
        if not isinstance(pair, list) or len(pair) != 2:
            raise ValueError(f"invalid portal_data item in {source}: {pair!r}")
        portal_edges.append(f"new MapEdge({cell_expr(pair[0], source)}, {cell_expr(pair[1], source)})")
    if walls: args.append("walls: [" + ", ".join(walls) + "]")
    if portal_edges: args.append("portals: [" + ", ".join(portal_edges) + "]")
    mechanisms = mechanism_expr(mapping, source)
    if mechanisms is not None:
        args.append("mechanisms: " + mechanisms)
    sight = mapping.get("camera_sight")
    if sight is not None:
        validate_sight(sight)
        args.append("cameraSight: new CameraSight(" + ", ".join(str(item) for item in sight) + ")")
    swipe = mapping.get("in_map_swipe_preset_data")
    if swipe is not None:
        if not isinstance(swipe, list) or len(swipe) != 2 or any(type(item) is not int for item in swipe):
            raise ValueError(f"in_map_swipe_preset_data must contain two integers in {source}")
        args.append("swipePreset: new SwipePreset(" + ", ".join(str(item) for item in swipe) + ")")
    grid_class = mapping.get("grid_class")
    if grid_class is not None:
        if not isinstance(grid_class, dict) or grid_class.get("$ref") not in GRID_TYPES:
            raise ValueError(f"unsupported grid_class in {source}: {grid_class!r}")
        reference = grid_class["$ref"]
        target, expected_hash = GRID_TYPES[reference]
        grid_source = reference.rsplit(".", 1)[0].replace(".", "/") + ".py"
        if source_hash(root, grid_source) != expected_hash:
            raise ValueError(f"custom grid source changed; review C# override: {grid_source}")
        args.append(f"createCell: static (cell, tile) => new {target}(cell, tile)")
    ignored = ignored_prediction_expr(calls, source)
    if ignored is not None:
        args.append("ignoredPredictions: " + ignored)
    args.append("name: " + literal(resolved["name"]))
    # One declaration per argument keeps generated rules reviewable.
    return (f'            [{literal(key)}] = new CampaignMapEntry({literal(key)}, new MapDefinition(\n'
            + ",\n".join("                " + arg for arg in args) + "),\n"
            + f"                new SourceFile({literal(source)}, {literal(source_hash(root, source))})),")


def generate(root: Path, output: Path, check: bool = False) -> None:
    entries: list[str] = []
    seen: set[str] = set()
    sources: set[str] = {"module/map/map_base.py", "module/map/utils.py", "module/map_detection/grid_info.py"}
    resolver = MapResolver(root)
    for path in sorted((root / "campaign").rglob("*.py")):
        source = path.relative_to(root).as_posix()
        if source in seen:
            continue
        seen.add(source)
        module = source.removesuffix(".py").replace("/", ".")
        sources.add(source)
        resolved = resolver.export(module)
        if not resolved.get("complete"):
            raise ValueError(f"unresolved MAP declaration in {source}: {resolved.get('unresolved')}")
        if not resolved.get("present"):
            continue
        sources.update(resolved["source_files"])
        entries.append(map_entry(root, source, resolved))
    if not entries:
        raise ValueError("upstream contains no complete MAP declarations")
    banner = "// <auto-generated>Compiled upstream map declarations; regenerate with tools/migration/compile_campaign_maps.py.</auto-generated>\n"
    content = banner + "using System.Collections.Frozen;\nusing System.Collections.Immutable;\nnamespace Alas.Engine.Rules;\n\n"
    content += "public sealed record CampaignMapEntry(string Id, MapDefinition Map, SourceFile Source);\n"
    content += "public static class CampaignMapCatalog\n{\n    private static readonly FrozenDictionary<string, CampaignMapEntry> Entries =\n"
    content += "        new Dictionary<string, CampaignMapEntry>(StringComparer.Ordinal)\n        {\n"
    content += "\n".join(entries) + "\n        }.ToFrozenDictionary(StringComparer.Ordinal);\n\n"
    content += "    public static IEnumerable<string> Ids => Entries.Keys.Order(StringComparer.Ordinal);\n"
    content += "    public static ImmutableArray<SourceFile> Sources { get; } =\n    [\n"
    content += "\n".join(f"        new({literal(s)}, {literal(source_hash(root, s))})," for s in sorted(sources))
    content += "\n    ];\n"
    content += "    public static CampaignMapEntry Get(string id) => Entries.TryGetValue(id, out var entry) ? entry : throw new NotSupportedException($\"C# map is not implemented: {id}\");\n"
    content += "}\n"
    normalized = content.replace("\r\n", "\n")
    if check:
        if not output.is_file() or output.read_text(encoding="utf-8-sig").replace("\r\n", "\n") != normalized:
            raise ValueError(f"C# map declaration drift: {output}")
    else:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(normalized, encoding="utf-8", newline="\n")
    print(f"Compiled C# maps {'verified' if check else 'written'}: {len(entries)}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=Path("src/Alas.Engine/Rules/Generated/CampaignMaps.g.cs"))
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    generate(args.upstream.resolve(), args.output.resolve(), args.check)
