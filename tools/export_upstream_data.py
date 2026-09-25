#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
上游数据契约导出器（路线甲 · S0）。

职责边界（重要）：
    本工具**不重写上游提取器**。上游的 `dev_tools/button_extract.py`、`map_extractor.py`
    等已经把「游戏资源 → Python 数据文件」这一步做完了；本工具只做
    「上游生成的 .py 产物 → JSON」这最后一段转换。

    因此数据同步链是：
        游戏资源 --(上游 Python 提取器)--> assets.py / campaign/*.py --(本工具)--> JSON
    上游更新素材或地图时，只需重跑上游提取器 + 本工具，C# 侧零改动。

用法：
    python export_upstream_data.py --repo <ALAS 仓库> --out <输出目录>
    python export_upstream_data.py --repo <...> --out <...> --check   # 校验产物是否与源码一致

产物：
    <out>/assets.json                  素材绑定（Button/Template，四服变体）
    <out>/campaign/<path>.json         关卡 IR（地图网格 + Config 标志 + 归一化战斗计划）
    <out>/campaign_index.json          关卡索引（含是否模板化、是否需要人工复核）
    <out>/schema/assets.schema.json    JSON Schema
    <out>/schema/campaign.schema.json  JSON Schema
    <out>/manifest.json                溯源：上游 commit、源文件哈希、计数、未解析项
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict

try:
    from .upstream_config_export import ConfigResolver
    from .upstream_map_export import MapResolver
    from .upstream_campaign_export import CampaignResolver
except ImportError:
    from upstream_config_export import ConfigResolver
    from upstream_map_export import MapResolver
    from upstream_campaign_export import CampaignResolver

EXPORTER_VERSION = '2.9.0'
SERVERS = ('cn', 'en', 'jp', 'tw')
SKIP_DIRS = {'.venv', '.git', '__pycache__', '.pytest_cache', '.ruff_cache', '.trial-merge'}

# 上游 dev_tools/map_extractor.py 里 battle_N 模板会吐出的调用词表。
# 只用到这些调用 = 该关卡是「生成器模板产物」；此分类仅用于离线摘要。
TEMPLATE_VOCAB = {'clear_siren', 'clear_filter_enemy', 'battle_default', 'clear_boss',
                  'fleet_boss.clear_boss'}




# --------------------------------------------------------------------- 工具
def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 16), b''):
            h.update(chunk)
    return h.hexdigest()


def name_from_path(rel: str) -> str:
    """关卡名兜底：campaign_main/campaign_1_2.py -> '1-2'。

    上游有的文件写 `MAP = CampaignMap()`（不带名字），名字另在别处给。
    这里从路径派生一个稳定标识，并在 IR 里用 name_source 标注来源，避免冒充权威。
    """
    stem = os.path.basename(rel)
    if stem.endswith('.py'):
        stem = stem[:-3]
    if stem.startswith('campaign_'):
        stem = stem[len('campaign_'):]
    if re.fullmatch(r'\d+_\d+', stem):
        return stem.replace('_', '-')
    return stem


def iter_py(root: str, subdir: str):
    base = os.path.join(root, subdir)
    for dirpath, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in sorted(files):
            if f.endswith('.py'):
                yield os.path.join(dirpath, f)


def literal(node):
    """尽力把 AST 节点求成字面量；失败返回哨兵。"""
    try:
        return ast.literal_eval(node)
    except Exception:
        return _UNRESOLVED


_UNRESOLVED = object()


def call_name(node: ast.AST):
    """self.a() -> 'a'；self.a.b() -> 'a.b'；Button(...) -> 'Button'。"""
    if not isinstance(node, ast.Call):
        return None
    f = node.func
    if isinstance(f, ast.Name):
        return f.id
    if isinstance(f, ast.Attribute):
        if isinstance(f.value, ast.Name):
            return f.attr if f.value.id != 'self' else f.attr
        if isinstance(f.value, ast.Attribute) and isinstance(f.value.value, ast.Name) \
                and f.value.value.id == 'self':
            return f'{f.value.attr}.{f.attr}'
        return f.attr
    return None


def is_self_call(node: ast.AST):
    """仅匹配 self.x() / self.a.b()。"""
    if not isinstance(node, ast.Call):
        return False
    f = node.func
    if isinstance(f, ast.Attribute) and isinstance(f.value, ast.Name) and f.value.id == 'self':
        return True
    return bool(isinstance(f, ast.Attribute) and isinstance(f.value, ast.Attribute)
                and isinstance(f.value.value, ast.Name) and f.value.value.id == 'self')


def is_super_delegate(node: ast.AST):
    """匹配 `super().x(...)`（纯委托给父类实现）。"""
    if not isinstance(node, ast.Call):
        return False
    f = node.func
    return bool(isinstance(f, ast.Attribute) and isinstance(f.value, ast.Call)
                and isinstance(f.value.func, ast.Name) and f.value.func.id == 'super')


def super_call_name(node: ast.Call):
    return f'super().{node.func.attr}'


def _campaign_module_path(root: str, module: str) -> str:
    return os.path.join(root, 'campaign', *module.split('.')) + '.py'


def _relative_import_origin(tree, current_module: str, name: str):
    """把基类名解析成 `(模块名, 原始类名)`；相对导入与 `campaign.` 绝对导入都支持。

    - `from .campaign_14_base import CampaignBase` → `('campaign_main.campaign_14_base', 'CampaignBase')`
    - `from campaign.campaign_main.campaign_14_base import CampaignBase` → 同上（上游确有这种绝对写法）
    - `from .campaign_15_4 import Campaign as Campaign_15_4` → `('campaign_main.campaign_15_4', 'Campaign')`
      —— **必须回原始类名**，否则按别名在基类模块里找不到类（实测踩到的坑）。
    """
    package = current_module.rsplit('.', 1)[0] if '.' in current_module else ''
    for node in tree.body:
        if not isinstance(node, ast.ImportFrom) or not node.module:
            continue
        for alias in node.names:
            if (alias.asname or alias.name) != name:
                continue
            if node.level:
                parts = package.split('.') if package else []
                up = node.level - 1
                if up:
                    parts = parts[:-up] if up <= len(parts) else []
                origin = '.'.join([p for p in parts if p] + [node.module])
            elif node.module.startswith('campaign.'):
                origin = node.module[len('campaign.'):]
            else:
                continue
            return origin, alias.name
    return None


def campaign_literal_attributes(tree, module: str, root: str, max_depth: int = 12) -> dict:
    """收集 `Campaign` 类**属性链**上的字面量（含相对导入的基类），纯静态解析。

    背景（实测）：关卡里的 `self.clear_filter_enemy(self.ENEMY_FILTER, preserve=1)` 这类实参
    定义在**基类**（如 `.campaign_14_base` 的 `CampaignBase.ENEMY_FILTER = '1T > 1L > …'`），
    而导出器原先只解析 `Campaign` 类自身声明，于是实参被记成 `'<expr>'`——C# 引擎无法执行
    （全库 970 个 `clear_filter_enemy` 步骤因此被阻塞）。

    这里**只取字面量**（模块级与类体里 `ast.literal_eval` 能算出的赋值），表达式一律忽略：
    解析不出时保持 `'<expr>'`，宁缺勿猜，不会写入错误的过滤串。不导入游戏代码。
    """
    values: dict = {}
    seen_modules: set = set()
    parsed: dict = {module: tree}
    pending = [(module, 'Campaign')]
    steps = 0
    while pending and steps < max_depth:
        steps += 1
        current_module, class_name = pending.pop(0)
        current = parsed.get(current_module)
        if current is None:
            path = _campaign_module_path(root, current_module)
            if not os.path.isfile(path):
                continue
            try:
                with open(path, encoding='utf-8') as source:
                    current = ast.parse(source.read())
            except (OSError, SyntaxError):
                continue
            parsed[current_module] = current

        if current_module not in seen_modules:
            seen_modules.add(current_module)
            for node in current.body:
                if isinstance(node, ast.Assign):
                    try:
                        value = ast.literal_eval(node.value)
                    except Exception:
                        continue
                    for target in node.targets:
                        if isinstance(target, ast.Name):
                            values.setdefault(target.id, value)

        for node in current.body:
            if not isinstance(node, ast.ClassDef) or node.name != class_name:
                continue
            for sub in node.body:
                if isinstance(sub, ast.Assign):
                    try:
                        value = ast.literal_eval(sub.value)
                    except Exception:
                        continue
                    for target in sub.targets:
                        if isinstance(target, ast.Name):
                            values.setdefault(target.id, value)
            for base in node.bases:
                base_name = base.id if isinstance(base, ast.Name) else None
                if not base_name:
                    continue
                origin = _relative_import_origin(current, current_module, base_name)
                if origin:
                    # origin 是 (模块名, 原始类名)：别名导入时原始类名与绑定名不同
                    pending.append(origin)
    return values


def attribute_literal_resolver(literals: dict):
    """把 `self.<NAME>` / `<NAME>` 实参节点解成字面量；解析不出返回哨兵。"""
    def resolve(node):
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) \
                and node.value.id == 'self' and node.attr in literals:
            return literals[node.attr]
        if isinstance(node, ast.Name) and node.id in literals:
            return literals[node.id]
        return _UNRESOLVED
    return resolve


def campaign_map_shape(tree) -> str:
    """模块级 `MAP.shape = 'K9'` 的字面量形状。"""
    for node in tree.body:
        if not isinstance(node, ast.Assign) or not isinstance(node.value, ast.Constant) \
                or not isinstance(node.value.value, str):
            continue
        for target in node.targets:
            if isinstance(target, ast.Attribute) and target.attr == 'shape':
                return node.value.value
    return ''


def campaign_symbol_locations(tree) -> dict:
    """`A1, B1, … = MAP.flatten()` 的符号 → `[x, y]` 表（带形状自校验，不通过就返回空表）。

    上游关卡用这一行元组解包绑定全部格子符号；形状（`MAP.shape = 'K9'`）决定列数与行数，
    符号顺序是行优先。**只做形状自校验通过的解析**：符号数必须等于 `列数 × 行数`，否则整表作废。
    """
    symbols = []
    for node in tree.body:
        if isinstance(node, ast.Assign) and isinstance(node.value, ast.Call) \
                and isinstance(node.value.func, ast.Attribute) and node.value.func.attr == 'flatten':
            for target in node.targets:
                if isinstance(target, ast.Tuple):
                    symbols = [e.id for e in target.elts if isinstance(e, ast.Name)]
    shape = campaign_map_shape(tree)
    letters = ''.join(ch for ch in shape if ch.isalpha())
    digits = ''.join(ch for ch in shape if ch.isdigit())
    if not symbols or len(letters) != 1 or not digits:
        return {}
    columns, rows = ord(letters.upper()) - ord('A') + 1, int(digits)
    if columns * rows != len(symbols):
        return {}
    return {name: [index % columns, index // columns] for index, name in enumerate(symbols)}


def campaign_grid_list_variables(tree, locations: dict) -> dict:
    """模块级 `name = SelectedGrids([符号…])` / `name = [符号…]` → `{name: [[x, y], …]}`。

    上游关卡常见写法：`step_on = SelectedGrids([E4, D3, G4, C3])`，随后
    `self.fleet_2_step_on(step_on, …)`——实参是**变量名**而不是符号本身。
    只解析"全是已知格子符号"的列表，解析不出就不进表（宁缺勿猜）。
    """
    variables = {}
    for node in tree.body:
        if not isinstance(node, ast.Assign):
            continue
        value = node.value
        if isinstance(value, ast.List):
            elements = value.elts
        elif isinstance(value, ast.Call) and isinstance(value.func, ast.Name) \
                and value.func.id == 'SelectedGrids' and len(value.args) == 1 \
                and isinstance(value.args[0], ast.List):
            elements = value.args[0].elts
        else:
            continue
        if not elements or not all(isinstance(e, ast.Name) and e.id in locations for e in elements):
            continue
        for target in node.targets:
            if isinstance(target, ast.Name):
                variables[target.id] = [locations[e.id] for e in elements]
    return variables


def symbol_argument_resolver(locations: dict, variables: dict | None = None):
    """裸格子符号 / 符号列表 / `SelectedGrids([符号…])` / 模块级格子表变量 → `__grid__` / `__grids__`。

    上游关卡里 `pick_up_flare(H9)`、`fleet_2_rescue(G2)`、`clear_map_items([F1, I1])`、
    `fleet_2_step_on(step_on, …)`（`step_on = SelectedGrids([E4, D3, G4, C3])`）这类实参传的是具体格子
    （原本只能记 `<expr>`）。解析不出时返回哨兵，照旧记 `<expr>`。
    """
    variables = variables or {}

    def resolve(node):
        if not locations:
            return _UNRESOLVED
        if isinstance(node, ast.Name):
            if node.id in variables:
                return {'__grids__': variables[node.id]}
            if node.id in locations:
                return {'__grid__': locations[node.id]}
        if isinstance(node, ast.List) and node.elts and all(
                isinstance(item, ast.Name) and item.id in locations for item in node.elts):
            return {'__grids__': [locations[item.id] for item in node.elts]}
        # `SelectedGrids([E4, D3, …])`：包装一层构造调用
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) \
                and node.func.id == 'SelectedGrids' and len(node.args) == 1 \
                and isinstance(node.args[0], ast.List) and node.args[0].elts \
                and all(isinstance(item, ast.Name) and item.id in locations for item in node.args[0].elts):
            return {'__grids__': [locations[item.id] for item in node.args[0].elts]}
        return _UNRESOLVED
    return resolve


def _parse_road_expr(node, locations: dict):
    """`RoadGrids([...])` 与其 `.combine(...)` 链 → blocks（`[[[x, y], …], …]`）；解析不出返回 None。

    上游 `RoadGrids.combine(road)` 的语义是**块的两两并集**（`SelectedGrids.add` 去重保序）：
    `out.grids = [b1.add(b2) for b1 in self.grids for b2 in road.grids]`，这里照抄。
    """
    if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == 'combine' \
            and len(node.args) == 1:
        left = _parse_road_expr(node.func.value, locations)
        right = _parse_road_expr(node.args[0], locations)
        if left is None or right is None:
            return None
        combined = []
        for block_left in left:
            for block_right in right:
                merged, seen = [], set()
                for cell in list(block_left) + list(block_right):
                    key = tuple(cell)
                    if key in seen:
                        continue
                    seen.add(key)
                    merged.append(cell)
                combined.append(merged)
        return combined or None

    if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == 'RoadGrids'
            and len(node.args) == 1 and isinstance(node.args[0], ast.List)):
        return None
    blocks = []
    for element in node.args[0].elts:
        if isinstance(element, ast.Name) and element.id in locations:
            blocks.append([locations[element.id]])
        elif isinstance(element, ast.List) and element.elts and all(
                isinstance(item, ast.Name) and item.id in locations for item in element.elts):
            blocks.append([locations[item.id] for item in element.elts])
        else:
            return None
    return blocks or None


def campaign_road_list_variables(tree, roads: dict) -> dict:
    """模块级 `roads = [road_a, road_b, …]` → `{roads: [road_a, road_b, …]}`（只收已知路段名）。"""
    variables = {}
    for node in tree.body:
        if not isinstance(node, ast.Assign) or not isinstance(node.value, ast.List):
            continue
        elements = node.value.elts
        if not elements or not all(isinstance(e, ast.Name) and e.id in roads for e in elements):
            continue
        for target in node.targets:
            if isinstance(target, ast.Name):
                variables[target.id] = [e.id for e in elements]
    return variables


def campaign_road_table(tree) -> dict:
    """模块级 `road_x = RoadGrids([...])`（含 `.combine(...)` 链）→ `{road_x: [[[x, y], …], …]}`。

    上游关卡用 `A1, B1, … = MAP.flatten()` 绑定格子符号，再用
    `road_main = RoadGrids([[H3, B6, C5]])` 声明路段（每个元素是一个 block：单格或格组）。
    `clear_roadblocks([road_main])` 这类调用的实参就是这些路段对象——标量字面量表达不了。

    **只做能静态解析的**：格子符号必须在形状自校验通过的符号表里，`combine` 两端都要能解析，
    否则该条路段不进表、实参照旧记 `<expr>`（宁缺勿猜）。
    """
    locations = campaign_symbol_locations(tree)
    if not locations:
        return {}

    roads = {}
    for node in tree.body:
        if not isinstance(node, ast.Assign):
            continue
        blocks = _parse_road_expr(node.value, locations)
        if not blocks:
            continue
        for target in node.targets:
            if isinstance(target, ast.Name):
                roads[target.id] = blocks
    return roads


def road_argument_resolver(roads: dict, road_lists: dict | None = None):
    """把 `road_a` / `[road_a, road_b]` / 路段列表变量解成 `{'__roads__': [路段, ...]}`；否则返回哨兵。"""
    road_lists = road_lists or {}

    def resolve(node):
        if not roads:
            return _UNRESOLVED
        # 空列表也**有语义**（"没有路障"）：实测 `fleet_2_step_on(SelectedGrids([A1]), roadblocks=[])`
        if isinstance(node, ast.List) and not node.elts:
            return {'__roads__': []}
        if isinstance(node, ast.Name):
            if node.id in roads:
                return {'__roads__': [roads[node.id]]}
            if node.id in road_lists:
                return {'__roads__': [roads[name] for name in road_lists[node.id]]}
            return _UNRESOLVED
        if isinstance(node, ast.List) and node.elts:
            expanded = []
            for item in node.elts:
                if not isinstance(item, ast.Name):
                    return _UNRESOLVED
                if item.id in roads:
                    expanded.append(roads[item.id])
                elif item.id in road_lists:
                    expanded.extend(roads[name] for name in road_lists[item.id])
                else:
                    return _UNRESOLVED
            return {'__roads__': expanded}
        return _UNRESOLVED
    return resolve


def parameter_defaults(node) -> dict:
    """方法签名 → `{参数名: 字面量默认值}`（没有默认值的参数记为 None）。

    只认能静态取值的默认值（`literal()` 能解出来的），解不出的记 None——不猜。
    `self` 与 `*args` / `**kwargs` 不进表。
    """
    if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
        return {}
    arguments = node.args
    names = [a.arg for a in arguments.posonlyargs + arguments.args if a.arg != 'self']
    defaults: list = [None] * (len(names) - len(arguments.defaults)) + list(arguments.defaults)
    table = {}
    for name, default in zip(names, defaults):
        if default is None:
            table[name] = None
            continue
        value = literal(default)
        table[name] = None if value is _UNRESOLVED else value
    for a, default in zip(arguments.kwonlyargs, arguments.kw_defaults):
        value = None if default is None else literal(default)
        table[a.arg] = None if value is _UNRESOLVED else value
    return table


def parameter_resolver(parameters: dict):
    """方法内**参数引用**（`super().handle_boss_appear_refocus(preset)`）→ `{'__param__': 'preset'}`。

    不把默认值直接内联进实参：默认值属于**钩子签名**（导出在 `parameters` 里），实参只记"传的是哪个参数"，
    这样"调用方显式传值"和"用了默认值"两种情况在计划里仍然可区分。
    """
    def resolve(node):
        if isinstance(node, ast.Name) and node.id in parameters:
            return {'__param__': node.id}
        return _UNRESOLVED
    return resolve


def call_args(node: ast.Call, resolve=None):
    """调用实参 → {位置参数: [...], 关键字参数: {...}}。

    字面量直接取；`self.<NAME>` 这类**类属性链上的字面量**经 `resolve` 解析（见
    `campaign_literal_attributes`）；其余非字面量仍记 `'<expr>'`。
    """
    def value_of(argument):
        value = literal(argument)
        if value is not _UNRESOLVED:
            return value
        if resolve is not None:
            resolved = resolve(argument)
            if resolved is not _UNRESOLVED:
                return resolved
        return '<expr>'

    pos, kw = [], {}
    for a in node.args:
        pos.append(value_of(a))
    for k in node.keywords:
        if k.arg is None:
            continue
        kw[k.arg] = value_of(k.value)
    return {'positional': pos, 'keyword': kw}


# --------------------------------------------------------------------- 素材
def export_assets(root: str, out_dir: str, manifest: dict):
    records = {}
    duplicates = defaultdict(list)
    names_seen = defaultdict(list)
    unresolved = []
    per_module = Counter()
    source_files = []

    for path in iter_py(root, 'module'):
        if os.path.basename(path) != 'assets.py':
            continue
        source_files.append(path)
        rel = os.path.relpath(path, root).replace('\\', '/')
        module = rel[len('module/'):-len('/assets.py')] if rel.count('/') else ''
        try:
            with open(path, encoding='utf-8') as source:
                tree = ast.parse(source.read())
        except SyntaxError as e:
            manifest['errors'].append({'file': rel, 'error': f'SyntaxError: {e}'})
            continue

        for node in tree.body:
            if not isinstance(node, ast.Assign) or len(node.targets) != 1:
                continue
            target = node.targets[0]
            if not isinstance(target, ast.Name):
                continue
            name = target.id
            kind = call_name(node.value) if isinstance(node.value, ast.Call) else None
            if kind not in ('Button', 'Template', 'Mask'):
                continue

            fields, bad = {}, []
            for k in node.value.keywords:
                v = literal(k.value)
                if v is _UNRESOLVED:
                    bad.append(k.arg)
                else:
                    fields[k.arg] = v
            if bad:
                unresolved.append({'asset': f'{module}/{name}', 'file': rel, 'fields': bad})
                continue

            # 归一：把 dict 形式的字段按键排序，并算出该绑定覆盖了哪些服
            norm = {}
            for key in ('area', 'color', 'button', 'file'):
                v = fields.get(key)
                if isinstance(v, dict):
                    norm[key] = {s: v[s] for s in SERVERS if s in v}
                elif v is not None:
                    norm[key] = v
            # 服别从**所有**分服字段取并集：Template 只有 file 没有 area，
            # 只看 area 会让 441 个 Template 的服务器信息丢失（实测踩过）。
            server_sets = [set(v) for v in norm.values() if isinstance(v, dict)]
            servers = sorted(set().union(*server_sets)) if server_sets else []

            # 结构校验：Button/Mask 必须有 area 才能定位；任何绑定都必须有 file
            if kind in ('Button', 'Mask') and not norm.get('area'):
                unresolved.append({'asset': f'{module}/{name}', 'file': rel,
                                   'issue': f'{kind} 缺 area'})
            if not norm.get('file'):
                unresolved.append({'asset': f'{module}/{name}', 'file': rel,
                                   'issue': '缺 file'})

            asset_id = f'{module}/{name}' if module else name
            names_seen[name].append(asset_id)
            records[asset_id] = {
                'id': asset_id,
                'name': name,
                'module': module,
                'kind': kind,
                **norm,
                'servers': servers,
                'all_servers': set(servers) >= set(SERVERS),
                'source': rel,
            }
            per_module[module] += 1

    os.makedirs(out_dir, exist_ok=True)
    _write_json(os.path.join(out_dir, 'assets.json'),
                {'version': EXPORTER_VERSION, 'servers': list(SERVERS),
                 'assets': records})

    manifest['assets'] = {
        'count': len(records),
        'all_four_servers': sum(1 for r in records.values() if r['all_servers']),
        'by_kind': dict(Counter(r['kind'] for r in records.values())),
        'by_module': dict(sorted(per_module.items())),
        'duplicate_names': {k: v for k, v in names_seen.items() if len(v) > 1},
        'unresolved': unresolved,
        'source_files': len(source_files),
        'source_hashes': {os.path.relpath(p, root).replace('\\', '/'): sha256_file(p)
                          for p in source_files},
    }
    return records


# --------------------------------------------------------------------- 关卡
def _is_bare_return_true(body) -> bool:
    """判断分支体是否只是 `return True`（上游生成器模板的唯一条件体形态）。"""
    stmts = [s for s in body
             if not (isinstance(s, ast.Expr) and isinstance(s.value, ast.Constant))]
    if len(stmts) != 1:
        return False
    s = stmts[0]
    return (isinstance(s, ast.Return) and isinstance(s.value, ast.Constant)
            and s.value.value is True)


def _local_reference(node, locals_):
    """把 `boss` / `boss[0]` 这类**局部变量引用**归一成 `{"__local__": …}`；不是局部就返回 None。"""
    if isinstance(node, ast.Name) and node.id in locals_:
        return {'__local__': node.id}
    if isinstance(node, ast.Subscript) and isinstance(node.value, ast.Name) \
            and node.value.id in locals_:
        index = node.slice
        if isinstance(index, ast.Constant) and isinstance(index.value, int):
            return {'__local__': node.value.id, '__index__': index.value}
    return None


def derive_plan(body: list, where: str, resolve=None):
    """
    把 battle_N 方法体归一成步骤序列。

    只归一**能完整表示**的语句形态：
        if self.X(args) and <body 仅为 return True>    → conditional
        if not self.X(args) and <body 仅为 return True> → conditional_negated
        return self.X(args)                            → terminal
        self.X(args)                                   → call
        var = self.X(args)                             → assign

    任何**表示不了**的（赋值运算、对变量取条件、分支体内还有别的语句、循环…）都记进 `unparsed`，
    此时 plan_complete=false 且 steps 作废。

    ⚠️ 这一条是**保真红线**，有真实教训：早期版本把
        `if not self.X(): return self.Y()`
    也当成 conditional_negated，结果把分支体里的 `self.Y()` **静默丢掉**——
    计划看起来完整，实际少调用一次，而当时的校验只查「计划里的算子在源码中出现」，
    查不出这种**丢步**。是 S3 解释器对拍（执行序列 vs 计划序列）才把它暴露出来（26 个关卡）。
    """
    # 方法内已绑定的**局部变量**（\oss = self.map.select(is_boss=True)\ 这类"观察"）。
    # 按 Python 语义向外层累积：外层绑定的名字在内层分支体里同样可见。
    locals_: set = set()

    def arg_resolve(node):
        """先看局部变量（含 `boss[0]` 这种下标），再走原来的字面量/路段/符号解析。"""
        local = _local_reference(node, locals_)
        return local if local is not None else resolve(node)

    def self_call_step(node, kind, **extra):
        s = {'op': call_name(node), 'args': call_args(node, arg_resolve), 'kind': kind}
        s.update(extra)
        return s

    def branch_test(test):
        """把 `if` 的条件归一成 `{"local": …}` 或 `{"call": …}`（带 `negate`）；表示不了返回 None。"""
        negate = False
        node = test
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
            negate = True
            node = node.operand
        local = _local_reference(node, locals_)
        if local is not None:
            return {'local': local['__local__'], 'negate': negate}
        if is_self_call(node):
            return {'call': {'op': call_name(node), 'args': call_args(node, arg_resolve)},
                    'negate': negate}
        return None

    def normalize(body):
        steps, unparsed, dead = [], [], []
        terminated = False
        for stmt in body:
            if terminated:
                # Python 语义：`return` 之后的语句**不可达**。
                # 上游确实存在这种手滑留下的死代码，实测 campaign/event_20211028_tw/c3.py：
                #     return self.battle_default()
                #     return self.battle_default()      ← 永不执行
                # 记进 dead 以便追溯，但绝不能当成步骤 —— 否则解释器会执行一次永不发生的调用
                # （实测会让解释器执行序列与计划序列对不上，2 个关卡）。
                dead.append(type(stmt).__name__)
                continue

            if isinstance(stmt, ast.If):
                t = stmt.test
                if not stmt.orelse and _is_bare_return_true(stmt.body):
                    if is_self_call(t):
                        steps.append(self_call_step(t, 'conditional'))
                        continue
                    if isinstance(t, ast.UnaryOp) and isinstance(t.op, ast.Not) \
                            and is_self_call(t.operand):
                        steps.append(self_call_step(t.operand, 'conditional_negated'))
                        continue
                # 分支体不是单纯的 `return True`：用 `branch` 步骤带上**嵌套体**
                # （上游 `battle_6` 一族：`if boss:` → `if not self.check_accessibility(boss[0], …):`）。
                test = branch_test(t)
                if test is None:
                    unparsed.append('If(cond)')
                    continue
                then_steps, then_unparsed, then_dead, then_end = normalize(stmt.body)
                else_steps, else_unparsed, else_dead, else_end = normalize(stmt.orelse)
                if then_unparsed or else_unparsed:
                    # 任一分支表示不了 → 整条 if 记为未解析（附加原因便于定位）
                    unparsed.append('If(nested)')
                    unparsed.extend(then_unparsed)
                    unparsed.extend(else_unparsed)
                    continue
                steps.append({'kind': 'branch', 'test': test,
                              'body': then_steps, 'orelse': else_steps})
                dead.extend(then_dead)
                dead.extend(else_dead)
                # 只有**两条分支都必然返回**时，后面的语句才不可达
                if then_end and (not stmt.orelse or else_end):
                    terminated = True
            elif isinstance(stmt, ast.Return):
                if stmt.value is not None and is_self_call(stmt.value):
                    steps.append(self_call_step(stmt.value, 'terminal'))
                    terminated = True
                elif is_super_delegate(stmt.value):
                    # `return super().X(...)`：纯委托，本类没有新增逻辑，只是覆写钩子。
                    steps.append({'op': super_call_name(stmt.value), 'args': call_args(stmt.value, resolve),
                                  'kind': 'super_delegate'})
                    terminated = True
                else:
                    unparsed.append('Return(expr)')
            elif isinstance(stmt, ast.Expr):
                if is_self_call(stmt.value):
                    steps.append(self_call_step(stmt.value, 'call'))
                else:
                    unparsed.append('Expr')
            elif isinstance(stmt, ast.Assign):
                v = stmt.value
                if is_self_call(v) and len(stmt.targets) == 1 \
                        and isinstance(stmt.targets[0], ast.Name):
                    target = stmt.targets[0].id
                    steps.append(self_call_step(v, 'assign', target=target))
                    # 记成局部变量：后面的 `if <name>:` / `<name>[0]` 才算得出来
                    locals_.add(target)
                else:
                    unparsed.append('Assign')
            elif isinstance(stmt, ast.Pass):
                continue
            else:
                unparsed.append(type(stmt).__name__)

        return steps, unparsed, dead, terminated

    steps, unparsed, dead, terminated = normalize(body)
    plan_complete = not unparsed
    if not plan_complete:
        steps = []
    return steps, plan_complete, unparsed, dead


def export_campaign(root: str, out_dir: str, manifest: dict):
    index, unresolved_all = [], []
    source_files = []
    stats = Counter()
    config_resolver = ConfigResolver(root)
    map_resolver = MapResolver(root)
    campaign_resolver = CampaignResolver(root)

    for path in iter_py(root, 'campaign'):
        rel = os.path.relpath(path, root).replace('\\', '/')
        source_files.append(path)
        try:
            with open(path, encoding='utf-8') as source:
                tree = ast.parse(source.read())
        except SyntaxError as e:
            manifest['errors'].append({'file': rel, 'error': f'SyntaxError: {e}'})
            continue

        module = rel[len('campaign/'):-3].replace('/', '.')
        # 类属性链上的字面量（如基类的 ENEMY_FILTER）与模块级路段（road_main = RoadGrids([...])），
        # 用于解析 self.<NAME> / [road_*] 实参；解析不出的实参仍记 '<expr>'，不猜值。
        literal_resolver = attribute_literal_resolver(campaign_literal_attributes(tree, module, root))
        road_table = campaign_road_table(tree)
        road_resolver = road_argument_resolver(road_table, campaign_road_list_variables(tree, road_table))
        grid_locations = campaign_symbol_locations(tree)
        symbol_resolver = symbol_argument_resolver(
            grid_locations, campaign_grid_list_variables(tree, grid_locations))

        def resolve_argument(node, _literal=literal_resolver, _road=road_resolver, _symbol=symbol_resolver):
            value = _literal(node)
            if value is not _UNRESOLVED:
                return value
            value = _road(node)
            return value if value is not _UNRESOLVED else _symbol(node)
        ir = {'source': rel, 'name': None, '_name_source': None, 'map': {}, 'config': {},
              'config_meta': {}, 'campaign': {'battles': [], 'attributes': {}},
              'unresolved': []}

        for node in tree.body:
            if isinstance(node, ast.ClassDef) and node.name == 'Campaign':
                for sub in node.body:
                    if isinstance(sub, (ast.FunctionDef, ast.AsyncFunctionDef)) \
                            and node.name == 'Campaign':
                        body = [x for x in sub.body if not (isinstance(x, ast.Expr)
                                and isinstance(x.value, ast.Constant))]
                        # 方法签名：参数名 + 字面量默认值。用途：`super().X(preset)` 这类委托把**参数引用**
                        # 解成 `{'__param__': 'preset'}`，再配上这里的默认值就能还原实参（见 campaign_1_1）。
                        parameters = parameter_defaults(sub)
                        parameter_ref = parameter_resolver(parameters)
                        def resolve_with_params(value, _base=resolve_argument, _ref=parameter_ref):
                            resolved = _ref(value)
                            return resolved if resolved is not _UNRESOLVED else _base(value)

                        steps, plan_complete, unparsed, dead = derive_plan(body, sub.name, resolve_with_params)
                        calls = []
                        for x in ast.walk(sub):
                            if is_self_call(x):
                                calls.append(call_name(x))
                        ir['campaign']['battles'].append({
                            'method': sub.name, 'calls': calls, 'steps': steps,
                            'plan_complete': plan_complete, 'unparsed': unparsed,
                            'dead_code': dead, 'parameters': parameters,
                            'stmt_count': len(body),
                        })
                if node.name == 'Campaign':
                    ir['campaign']['class'] = node.name
                    ir['campaign']['bases'] = [ast.unparse(b) for b in node.bases]

        declarations = campaign_resolver.export('campaign.' + module)
        ir['campaign']['attributes'] = declarations['values']
        ir['campaign']['attributes_meta'] = {k: v for k, v in declarations.items() if k != 'values'}
        # **上游自身无法导入**这种情况要把事实记下来（不能靠白名单，也不能只在检查脚本里猜）：
        # 关卡文件引用了 `module.campaign.assets` 里不存在的常量时（上游死代码），
        # `CampaignResolver` 的 unresolved 原因就是 `cannot import X from module.campaign.assets`。
        # 记在 attributes_meta 里供 Python 与 C# 两侧的验收命令共用同一份判据。
        import_failures = [issue.get('reason', '') for issue in declarations['unresolved']
                           if 'cannot import' in str(issue.get('reason', ''))]
        if import_failures:
            ir['campaign']['attributes_meta']['upstream_import_error'] = import_failures[0]
        for issue in declarations['unresolved']:
            prefix = 'Campaign.' + issue['field'] if 'field' in issue else 'Campaign'
            ir['unresolved'].append(f"{prefix}: {issue.get('reason', issue)}")

        # Declaration metadata is separate from native runtime map objects.
        map_export = map_resolver.export('campaign.' + module)
        ir['map'] = map_export['values']
        ir['map_meta'] = {key: map_export[key] for key in (
            'present', 'complete', 'origins', 'typed_values', 'source_files',
            'unresolved', 'derived_from', 'calls')}
        if map_export['name'] is not None:
            ir['name'], ir['_name_source'] = map_export['name'], 'CampaignMap'
        for issue in map_export['unresolved']:
            prefix = 'MAP.' + issue['field'] if 'field' in issue else 'MAP'
            ir['unresolved'].append(f"{prefix}: {issue.get('reason', issue)}")

        # Config is a separate inheritance graph from Campaign. Resolve the effective
        # chapter overrides from source so imported/re-exported Config classes, C3
        # inheritance and constant expressions are preserved in one general path.
        config_export = config_resolver.export('campaign.' + module)
        ir['config'] = config_export['values']
        ir['config_meta'] = {
            key: config_export[key]
            for key in ('present', 'complete', 'mro', 'origins', 'typed_values',
                        'source_files', 'unresolved')
        }
        if not config_export['complete']:
            for issue in config_export['unresolved']:
                field = issue.get('field')
                prefix = f'Config.{field}' if field else 'Config'
                ir['unresolved'].append(f"{prefix}: {issue.get('reason', issue)}")

        # 关卡名兜底
        if not ir['name']:
            ir['name'] = name_from_path(rel)
            ir['_name_source'] = 'path'

        # 派生字段与难度分级
        #
        # 分级只看 battle_* 方法（它们才驱动战斗计划）；非 battle_* 的覆写是「引擎钩子」，
        # 单独统计成 native_overrides —— 纯 `return super().X()` 的委托不算新增逻辑。
        battles = ir['campaign']['battles']
        battle_methods = [b for b in battles if b['method'].startswith('battle_')]
        hooks = [b for b in battles if not b['method'].startswith('battle_')]
        boss = None
        for b in battle_methods:
            if b['method'][7:].isdigit():
                boss = max(boss or 0, int(b['method'][7:]))
        all_calls = {c for b in battle_methods for c in b['calls']}
        plan_complete = bool(battle_methods) and all(b['plan_complete'] for b in battle_methods)
        template_only = plan_complete and all_calls <= TEMPLATE_VOCAB
        # 难度分级：A/B/C 仅衡量静态摘要完整度，运行时均复用上游原生实现
        tier = 'A' if template_only else ('B' if plan_complete else 'C')
        native_overrides = sorted(b['method'] for b in hooks if not b['plan_complete'])
        super_delegates = sorted(b['method'] for b in hooks if b['plan_complete'])
        ir['campaign']['boss_battle'] = boss
        ir['campaign']['template_only'] = template_only
        ir['campaign']['plan_complete'] = plan_complete
        ir['campaign']['tier'] = tier
        ir['campaign']['has_siren'] = 'clear_siren' in all_calls
        ir['campaign']['native_overrides'] = native_overrides
        ir['campaign']['super_delegates'] = super_delegates
        if not battle_methods:
            ir['unresolved'].append('无 Campaign.battle_* 方法')
        if ir['unresolved']:
            unresolved_all.append({'file': rel, 'items': ir['unresolved']})

        stats['files'] += 1
        stats['template_only'] += 1 if template_only else 0
        stats['with_unresolved'] += 1 if ir['unresolved'] else 0
        stats[f'tier_{tier}'] += 1
        stats['native_override_methods'] += len(native_overrides)
        stats['super_delegate_methods'] += len(super_delegates)
        stats['incomplete_battles'] += sum(1 for b in battle_methods if not b['plan_complete'])

        ir['name_source'] = ir.get('_name_source')
        name_source = ir.pop('_name_source', None)

        dest = os.path.join(out_dir, 'campaign', rel[len('campaign/'):-3] + '.json')
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        _write_json(dest, ir)

        index.append({
            'source': rel,
            'json': os.path.relpath(dest, out_dir).replace('\\', '/'),
            'name': ir['name'],
            'name_source': name_source,
            'tier': tier,
            'plan_complete': plan_complete,
            'template_only': template_only,
            'boss_battle': boss,
            'has_siren': ir['campaign']['has_siren'],
            'battle_methods': [b['method'] for b in battle_methods],
            'native_overrides': native_overrides,
            'super_delegates': super_delegates,
            'config_keys': sorted(ir['config'].keys()),
            'config_present': ir['config_meta']['present'],
            'config_complete': (ir['config_meta']['present']
                               and ir['config_meta']['complete']),
            'map_keys': sorted(ir['map'].keys()),
            'map_present': ir['map_meta']['present'],
            'map_complete': ir['map_meta']['present'] and ir['map_meta']['complete'],
            'campaign_attributes': sorted(declarations['values']),
            'campaign_present': declarations['present'],
            'campaign_complete': declarations['present'] and declarations['complete'],
            'campaign_aliases': sorted(declarations['method_aliases']),
            'needs_review': tier == 'C' or not ir['config_meta']['complete']
                            or not ir['map_meta']['complete'] or not declarations['complete'],
        })

    index.sort(key=lambda r: r['source'])
    _write_json(os.path.join(out_dir, 'campaign_index.json'),
                {'version': EXPORTER_VERSION, 'chapters': index})

    manifest['campaign'] = {
        **stats,
        'config_modules': sum(1 for r in index if r['config_present']),
        'config_complete': sum(1 for r in index
                               if r['config_present'] and r['config_complete']),
        'config_fields': sum(len(r['config_keys']) for r in index),
        'map_modules': sum(1 for r in index if r['map_present']),
        'map_complete': sum(1 for r in index if r['map_complete']),
        'map_fields': sum(len(r['map_keys']) for r in index),
        'campaign_modules': sum(r['campaign_present'] for r in index),
        'campaign_complete': sum(r['campaign_complete'] for r in index),
        'campaign_attributes': sum(len(r['campaign_attributes']) for r in index),
        'campaign_aliases': sum(len(r['campaign_aliases']) for r in index),
        'template_only_pct': round(100 * stats['template_only'] / max(stats['files'], 1), 1),
        'needs_review': sum(1 for r in index if r['needs_review']),
        'unresolved_detail': unresolved_all,
        'source_files': len(source_files),
        'source_hashes': {os.path.relpath(p, root).replace('\\', '/'): sha256_file(p)
                          for p in source_files},
    }
    return index


# --------------------------------------------------------------------- Schema
ASSETS_SCHEMA = {
    '$schema': 'https://json-schema.org/draft/2020-12/schema',
    'title': 'ALAS upstream asset bindings',
    'type': 'object',
    'required': ['version', 'servers', 'assets'],
    'properties': {
        'version': {'type': 'string'},
        'servers': {'type': 'array', 'items': {'enum': list(SERVERS)}},
        'assets': {
            'type': 'object',
            'additionalProperties': {
                'type': 'object',
                'required': ['id', 'name', 'module', 'kind', 'servers', 'source'],
                'properties': {
                    'id': {'type': 'string'},
                    'name': {'type': 'string'},
                    'module': {'type': 'string'},
                    'kind': {'enum': ['Button', 'Template', 'Mask']},
                    'area': {'$ref': '#/$defs/perServerArea'},
                    'button': {'$ref': '#/$defs/perServerArea'},
                    'color': {'$ref': '#/$defs/perServerColor'},
                    'file': {'$ref': '#/$defs/perServerPath'},
                    'servers': {'type': 'array', 'items': {'enum': list(SERVERS)}},
                    'all_servers': {'type': 'boolean'},
                    'source': {'type': 'string'},
                },
            },
        },
    },
    '$defs': {
        'area4': {'type': 'array', 'prefixItems': [{'type': 'integer'}] * 4,
                  'minItems': 4, 'maxItems': 4},
        'color3': {'type': 'array', 'prefixItems': [{'type': 'integer'}] * 3,
                   'minItems': 3, 'maxItems': 3},
        'perServerArea': {'type': 'object',
                          'additionalProperties': {'$ref': '#/$defs/area4'}},
        'perServerColor': {'type': 'object',
                           'additionalProperties': {'$ref': '#/$defs/color3'}},
        'perServerPath': {'type': 'object',
                          'additionalProperties': {'type': 'string'}},
    },
}

CAMPAIGN_SCHEMA = {
    '$schema': 'https://json-schema.org/draft/2020-12/schema',
    'title': 'ALAS campaign IR',
    'type': 'object',
    'required': ['source', 'map', 'map_meta', 'config', 'config_meta', 'campaign'],
    'properties': {
        'source': {'type': 'string'},
        'name': {'type': ['string', 'null'],
                 'description': "CampaignMap('1-2') 里的关卡名"},
        'name_source': {'enum': ['CampaignMap', 'path'],
                        'description': 'path = 从文件名兜底派生，不是上游的权威名字'},
        'map': {'type': 'object', 'additionalProperties': True},
        'map_meta': {
            'type': 'object',
            'required': ['present', 'complete', 'origins', 'typed_values',
                         'source_files', 'unresolved', 'derived_from', 'calls'],
            'properties': {
                'present': {'type': 'boolean'},
                'complete': {'type': 'boolean'},
                'derived_from': {'type': ['string', 'null']},
                'calls': {'type': 'array', 'items': {'type': 'object',
                    'required': ['method', 'args', 'kwargs', 'typed_args', 'typed_kwargs', 'origin']}},
                'origins': {'type': 'object', 'additionalProperties': {
                    'type': 'object', 'required': ['module', 'line', 'expression'],
                    'properties': {'module': {'type': 'string'},
                                   'line': {'type': 'integer', 'minimum': 1},
                                   'expression': {'type': 'string'}},
                }},
                'typed_values': {'type': 'object', 'additionalProperties': True},
                'source_files': {'type': 'array', 'items': {'type': 'string'}},
                'unresolved': {'type': 'array', 'items': {'type': 'object'}},
            },
            'description': 'MAP 源声明的离线证据；grid 与 class 引用保留符号类型，运行时仍使用原生对象',
        },
        'config': {'type': 'object', 'additionalProperties': True},
        'config_meta': {
            'type': 'object',
            'required': ['present', 'complete', 'mro', 'origins', 'typed_values',
                         'source_files', 'unresolved'],
            'properties': {
                'present': {'type': 'boolean'},
                'complete': {'type': 'boolean'},
                'mro': {'type': 'array', 'items': {'type': 'string'}},
                'origins': {'type': 'object', 'additionalProperties': {
                    'type': 'object',
                    'required': ['module', 'class', 'line', 'expression'],
                    'properties': {
                        'module': {'type': 'string'},
                        'class': {'type': 'string'},
                        'line': {'type': 'integer'},
                        'expression': {'type': 'string'},
                    },
                }},
                'typed_values': {'type': 'object', 'additionalProperties': True},
                'source_files': {'type': 'array', 'items': {'type': 'string'}},
                'unresolved': {'type': 'array', 'items': {'type': 'object'}},
            },
        },
        'campaign': {
            'type': 'object',
            'required': ['battles', 'attributes', 'attributes_meta'],
            'properties': {
                'class': {'type': 'string'},
                'bases': {'type': 'array', 'items': {'type': 'string'}},
                'boss_battle': {'type': ['integer', 'null']},
                'plan_complete': {
                    'type': 'boolean',
                    'description': 'true = 所有 battle_* 方法体都被归一成 steps，无未识别语句'},
                'template_only': {
                    'type': 'boolean',
                    'description': 'true = 静态步骤均落在模板词表；不代表可以替代原生运行时'},
                'tier': {
                    'enum': ['A', 'B', 'C'],
                    'description': 'A=模板词表内摘要；B=词表外算子摘要；C=含未解析逻辑；均由原生运行时执行',
                },
                'has_siren': {'type': 'boolean'},
                'attributes': {
                    'type': 'object',
                    'additionalProperties': True,
                    'description': 'Campaign 自身声明的数据属性；继承行为仍由原生 MRO 决定',
                },
                'attributes_meta': {
                    'type': 'object',
                    'required': ['scope', 'present', 'complete', 'class_reference', 'origins',
                                 'typed_values', 'method_aliases', 'source_files', 'unresolved'],
                    'properties': {
                        'scope': {'const': 'declared'},
                        'present': {'type': 'boolean'},
                        'complete': {'type': 'boolean'},
                        'class_reference': {'type': ['string', 'null']},
                        'origins': {'type': 'object', 'additionalProperties': {'type': 'object'}},
                        'typed_values': {'type': 'object'},
                        'method_aliases': {'type': 'object', 'additionalProperties': {'type': 'object',
                            'required': ['module', 'name'], 'properties': {
                                'module': {'type': 'string'}, 'name': {'type': 'string'}}}},
                        'source_files': {'type': 'array', 'items': {'type': 'string'}},
                        'unresolved': {'type': 'array', 'items': {'type': 'object'}},
                    },
                },
                'native_overrides': {
                    'type': 'array', 'items': {'type': 'string'},
                    'description': '非 battle_* 的覆写钩子，由原生 Campaign 继承调度保留'},
                'super_delegates': {
                    'type': 'array', 'items': {'type': 'string'},
                    'description': '纯 return super().X() 的覆写，由原生虚方法分派保留'},
                'battles': {
                    'type': 'array',
                    'items': {
                        'type': 'object',
                        'required': ['method', 'calls'],
                        'properties': {
                            'method': {'type': 'string'},
                            'calls': {'type': 'array', 'items': {'type': 'string'}},
                            'steps': {
                                'type': 'array',
                                'description': 'plan_complete=false 时恒为空数组，防止残缺计划被误用',
                                'items': {
                                    'type': 'object',
                                    'required': ['op', 'kind'],
                                    'properties': {
                                        'op': {'type': 'string'},
                                        'kind': {'enum': ['conditional',
                                                          'conditional_negated',
                                                          'terminal', 'call', 'assign',
                                                          'super_delegate']},
                                        'args': {'type': 'object'},
                                        'target': {'type': 'string'},
                                    },
                                },
                            },
                            'plan_complete': {'type': 'boolean'},
                            'unparsed': {'type': 'array', 'items': {'type': 'string'},
                                         'description': '未能归一的语句类型，空数组才代表计划完整'},
                            'stmt_count': {'type': 'integer'},
                        },
                    },
                },
            },
        },
        'unresolved': {'type': 'array', 'items': {'type': 'string'}},
    },
}


# --------------------------------------------------------------------- 落盘
def _write_json(path: str, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(obj, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write('\n')


def git_rev(repo: str):
    try:
        top = subprocess.run(['git', '-C', repo, 'rev-parse', '--show-toplevel'],
                             capture_output=True, text=True, timeout=30)
        # A runtime snapshot inside the host checkout is not an upstream Git
        # checkout. Never label its sources with the enclosing host commit.
        if top.returncode or os.path.normcase(os.path.realpath(top.stdout.strip())) != \
                os.path.normcase(os.path.realpath(repo)):
            return None, None
        out = subprocess.run(['git', '-C', repo, 'rev-parse', 'HEAD'],
                             capture_output=True, text=True, timeout=30)
        if out.returncode:
            return None, None
        rev = out.stdout.strip()
        out2 = subprocess.run(['git', '-C', repo, 'status', '--porcelain'],
                              capture_output=True, text=True, timeout=30)
        return rev, out2.stdout.strip() if out2.returncode == 0 else None
    except Exception as e:
        return None, None


def main():
    ap = argparse.ArgumentParser(description='导出上游数据契约为 JSON')
    default_repo = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..', '.runtime', 'engine')
    ap.add_argument('--repo', default=os.path.normpath(default_repo))
    ap.add_argument('--out', default=os.path.normpath(
        os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data')))
    ap.add_argument('--check', action='store_true',
                    help='重新计算并与磁盘产物比对，不写入（用于 CI 检测上游数据漂移）')
    args = ap.parse_args()

    if not os.path.isdir(os.path.join(args.repo, 'module')):
        print(f'不是有效的 ALAS 仓库: {args.repo}', file=sys.stderr)
        return 2

    rev, dirty = git_rev(args.repo)
    manifest = {
        'exporter_version': EXPORTER_VERSION,
        'upstream_repo': os.path.abspath(args.repo),
        'upstream_commit': rev,
        'upstream_dirty': bool(dirty) if dirty is not None else None,
        'errors': [],
    }

    if args.check:
        tmp = os.path.join(args.out, '__check_tmp__')
        _run_export(args.repo, tmp, manifest)
        diffs = _compare_dirs(os.path.join(args.out), tmp)
        _rmtree(tmp)
        print(json.dumps({'mode': 'check', 'differences': diffs,
                          'ok': not diffs}, ensure_ascii=False, indent=2))
        return 0 if not diffs else 1

    _run_export(args.repo, args.out, manifest)
    summary = {
        'mode': 'export',
        'out': os.path.abspath(args.out),
        'upstream_commit': rev,
        'upstream_dirty': bool(dirty) if dirty is not None else None,
        'assets': {k: v for k, v in manifest['assets'].items()
                   if k not in ('source_hashes', 'by_module', 'unresolved')},
        'campaign': {k: v for k, v in manifest['campaign'].items()
                     if k not in ('source_hashes', 'unresolved_detail')},
        'errors': manifest['errors'],
        'assets_unresolved': len(manifest['assets']['unresolved']),
        'campaign_unresolved_files': len(manifest['campaign']['unresolved_detail']),
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


def _run_export(repo, out, manifest):
    for name in ('assets', 'campaign'):
        manifest[name] = {}
    export_assets(repo, out, manifest)
    export_campaign(repo, out, manifest)
    _write_json(os.path.join(out, 'schema', 'assets.schema.json'), ASSETS_SCHEMA)
    _write_json(os.path.join(out, 'schema', 'campaign.schema.json'), CAMPAIGN_SCHEMA)
    _write_json(os.path.join(out, 'manifest.json'), manifest)


def _compare_dirs(a, b):
    """
    只比对**本导出器自己的产物**。

    注意不要把整个输出目录都拿进来比：data/ 下还会有别的工具产物
    （例如 tools/make_imaging_fixture.py 生成的 fixtures/），
    它们不在临时目录里，会被误报成差异（实测踩过）。
    """
    OWNED_FILES = {'assets.json', 'campaign_index.json', 'manifest.json'}
    OWNED_DIRS = ('campaign/', 'schema/', 'rules/')

    def owned(rel):
        return rel in OWNED_FILES or rel.startswith(OWNED_DIRS)

    diffs = []

    def walk(base):
        found = set()
        for dirpath, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if d != '__check_tmp__']
            for f in files:
                rel = os.path.relpath(os.path.join(dirpath, f), base).replace('\\', '/')
                if owned(rel):
                    found.add(rel)
        return found

    fa, fb = walk(a), walk(b)
    for rel in sorted(fa ^ fb):
        diffs.append({'file': rel, 'issue': 'only-in-one-side'})
    for rel in sorted(fa & fb):
        pa, pb = os.path.join(a, rel), os.path.join(b, rel)
        if rel == 'manifest.json':
            # Paths/working-tree state are machine-specific. The remaining
            # provenance (including hashes) must participate in drift checks.
            def stable_manifest(path):
                with open(path, encoding='utf-8') as stream:
                    result = json.load(stream)
                for key in ('upstream_repo', 'upstream_dirty'):
                    result.pop(key, None)
                return result
            if stable_manifest(pa) != stable_manifest(pb):
                diffs.append({'file': rel, 'issue': 'provenance-differs'})
            continue
        if open(pa, 'rb').read() != open(pb, 'rb').read():
            diffs.append({'file': rel, 'issue': 'content-differs'})
    return diffs


def _rmtree(path):
    import shutil
    shutil.rmtree(path, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
