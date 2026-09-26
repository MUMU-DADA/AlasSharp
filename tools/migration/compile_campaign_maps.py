"""Generate compiled C# map declarations from the pinned upstream campaign snapshot.

This is a build-time source generator. The product never reads campaign JSON and
never interprets a plan. Unsupported map mechanisms fail generation instead of
silently becoming a default map.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from upstream_map_export import MapResolver


def literal(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def source_hash(root: Path, relative: str) -> str:
    return hashlib.sha256((root / relative).read_bytes()).hexdigest()


def parse_weights(value: object, count: int) -> list[str] | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise ValueError("weight_data must be text")
    raw = value.split()
    if len(raw) != count:
        raise ValueError(f"weight_data has {len(raw)} values, expected {count}")
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
        if not isinstance(wave, dict) or not isinstance(wave.get("battle"), int):
            raise ValueError(f"invalid spawn wave in {source}")
        values = [str(wave["battle"])] + [str(int(wave.get(k, 0))) for k in ("enemy", "mystery", "boss", "siren")]
        result.append(f"new({', '.join(values)})")
    return "[" + ", ".join(result) + "]"


def wall_edges(value: object, source: str) -> list[str]:
    if not value:
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
    return [f"new MapEdge({literal(f'{a[0]}')!r}, {literal(f'{b[0]}')!r})" for a, b in []] if False else [
        f'new MapEdge("{chr(64 + a[0])}{a[1]}", "{chr(64 + b[0])}{b[1]}")' for a, b in edges]


def cell_list(value: object) -> list[str]:
    if value is None: return []
    if isinstance(value, str): return [value]
    if isinstance(value, list): return [str(item) for item in value if isinstance(item, str)]
    raise ValueError(f"invalid cell list: {value!r}")


def cell_expr(value: object, source: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[A-Z]+[1-9][0-9]*", value):
        raise ValueError(f"invalid map cell in {source}: {value!r}")
    return f'Cell.Parse("{value}")'


def cell_group_expr(value: object, source: str) -> str:
    cells = cell_list(value)
    if not cells:
        raise ValueError(f"empty mechanism cell group in {source}")
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
    if land:
        if not isinstance(land, list):
            raise ValueError(f"land_based_data must be a list in {source}")
        declarations = []
        for item in land:
            if not isinstance(item, list) or len(item) != 2 or str(item[1]).lower() not in {"up", "down", "left", "right"}:
                raise ValueError(f"invalid land_based_data item in {source}: {item!r}")
            declarations.append(f'new LandMechanism({cell_expr(item[0], source)}, MapDirection.{str(item[1]).title()})')
        args.append("landBased: [" + ", ".join(declarations) + "]")
    return "new MapMechanisms(" + ", ".join(args) + ")" if args else None


def ignored_prediction_expr(calls: list[dict], source: str) -> str | None:
    predicates: list[str] = []
    names = {"enemy_scale": "EnemyScale", "enemy_genre": "EnemyGenre", "is_siren": "IsSiren"}
    for call in calls:
        if call.get("method") != "ignore_prediction" or len(call.get("args", [])) != 1:
            raise ValueError(f"unsupported MAP call in {source}: {call!r}")
        cell = cell_expr(call["args"][0], source)
        checks = []
        for name, value in call.get("kwargs", {}).items():
            if name not in names or not isinstance(value, (bool, int, str)):
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


def map_entry(root: Path, source: str, mapping: dict, calls: list[dict]) -> str | None:
    if not isinstance(mapping, dict) or not isinstance(mapping.get("map_data"), str):
        return None
    map_text = mapping["map_data"]
    map_rows = [line for line in map_text.splitlines() if line.split()]
    if not map_rows:
        raise ValueError(f"empty map in {source}")
    rows = len(map_rows)
    columns = 0
    for line in map_rows:
        tokens = line.split()
        columns = columns or len(tokens)
        if len(tokens) != columns:
            raise ValueError(f"ragged map in {source}")
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
    weights = parse_weights(mapping.get("weight_data"), columns * rows)
    cameras = mapping.get("camera_data") or []
    spawn_cameras = mapping.get("camera_data_spawn_point") or []
    spawn_data = mapping.get("spawn_data") or []
    if not isinstance(cameras, list) or not isinstance(spawn_cameras, list) or not isinstance(spawn_data, list):
        raise ValueError(f"invalid camera/spawn declaration in {source}")
    wave_text = spawn_waves(spawn_data, source)
    key = source.removeprefix("campaign/").removesuffix(".py").replace("\\", "/")
    args = [literal(shape), literal(mapping["map_data"]),
            "[" + ", ".join(literal(x) for x in cameras) + "]",
            "[" + ", ".join(literal(x) for x in spawn_cameras) + "]",
            wave_text]
    if mapping.get("map_data_loop") is not None:
        args.append("loopTiles: " + literal(mapping["map_data_loop"]))
    if weights is not None:
        args.append("weights: [" + ", ".join(weights) + "]")
    loop_waves = mapping.get("spawn_data_loop")
    if loop_waves:
        args.append("loopWaves: " + spawn_waves(loop_waves, source))
    covered = cell_list(mapping.get("map_covered"))
    if covered:
        args.append("covered: [" + ", ".join(literal(x) for x in covered) + "]")
    walls = wall_edges(mapping.get("wall_data"), source)
    portals = mapping.get("portal_data") or []
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
        if not isinstance(sight, list) or len(sight) != 4 or any(type(item) is not int for item in sight):
            raise ValueError(f"camera_sight must contain four integers in {source}")
        args.append("cameraSight: new CameraSight(" + ", ".join(str(item) for item in sight) + ")")
    swipe = mapping.get("in_map_swipe_preset_data")
    if swipe is not None:
        if not isinstance(swipe, list) or len(swipe) != 2 or any(type(item) is not int for item in swipe):
            raise ValueError(f"in_map_swipe_preset_data must contain two integers in {source}")
        args.append("swipePreset: new SwipePreset(" + ", ".join(str(item) for item in swipe) + ")")
    grid_class = mapping.get("grid_class")
    if grid_class is not None:
        if not isinstance(grid_class, dict) or not str(grid_class.get("$ref", "")).endswith("W15GridInfo"):
            raise ValueError(f"unsupported grid_class in {source}: {grid_class!r}")
        args.append("gridBehavior: MapGridBehavior.W15")
    ignored = ignored_prediction_expr(calls, source)
    if ignored is not None:
        args.append("ignoredPredictions: " + ignored)
    return (f"            [\"{key}\"] = new CampaignMapEntry(\"{key}\", new MapDefinition({', '.join(args)}), "
            f"new SourceFile({literal(source)}, {literal(source_hash(root, source))})),")


def generate(root: Path, output: Path, check: bool = False) -> None:
    entries: list[str] = []
    seen: set[str] = set()
    resolver = MapResolver(root)
    for path in sorted((root / "campaign").rglob("*.py")):
        source = path.relative_to(root).as_posix()
        if source in seen:
            continue
        seen.add(source)
        module = source.removesuffix(".py").replace("/", ".")
        resolved = resolver.export(module)
        if not resolved.get("present"):
            continue
        if not resolved.get("complete"):
            raise ValueError(f"unresolved MAP declaration in {source}: {resolved.get('unresolved')}")
        result = map_entry(root, source, resolved["values"], resolved["calls"])
        if result is not None:
            entries.append(result)
    if len(entries) < 1000:
        raise ValueError(f"unexpectedly incomplete map generation: {len(entries)}")
    banner = "// <auto-generated>Compiled upstream map declarations; regenerate with tools/migration/compile_campaign_maps.py.</auto-generated>\n"
    content = banner + "using System.Collections.Frozen;\nnamespace Alas.Engine.Rules;\n\n"
    content += "public sealed record CampaignMapEntry(string Id, MapDefinition Map, SourceFile Source);\n"
    content += "public static class CampaignMapCatalog\n{\n    private static readonly FrozenDictionary<string, CampaignMapEntry> Entries =\n"
    content += "        new Dictionary<string, CampaignMapEntry>(StringComparer.Ordinal)\n        {\n"
    content += "\n".join(entries) + "\n        }.ToFrozenDictionary(StringComparer.Ordinal);\n\n"
    content += "    public static IEnumerable<string> Ids => Entries.Keys.Order(StringComparer.Ordinal);\n"
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
