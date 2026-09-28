#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Historical Python plan normalizer.

This module is retained only as an offline oracle for archived S3/R5 reports.
It is deliberately outside the active exporter and is never imported by the
Engine product path. Runtime rules are compiled into typed C# rule classes.
"""
from __future__ import annotations

import ast
import os
import re

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
                and isinstance(f.value.func, ast.Name) and f.value.func.id == 'super'
                and not f.value.args and not f.value.keywords)
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
def campaign_symbol_locations(tree, resolved_shape: str | None = None) -> dict:
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
    shape = campaign_map_shape(tree, resolved_shape)
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
def campaign_road_table(tree, resolved_shape: str | None = None) -> dict:
    """模块级 `road_x = RoadGrids([...])`（含 `.combine(...)` 链）→ `{road_x: [[[x, y], …], …]}`。

    上游关卡用 `A1, B1, … = MAP.flatten()` 绑定格子符号，再用
    `road_main = RoadGrids([[H3, B6, C5]])` 声明路段（每个元素是一个 block：单格或格组）。
    `clear_roadblocks([road_main])` 这类调用的实参就是这些路段对象——标量字面量表达不了。

    **只做能静态解析的**：格子符号必须在形状自校验通过的符号表里，`combine` 两端都要能解析，
    否则该条路段不进表、实参照旧记 `<expr>`（宁缺勿猜）。
    """
    locations = campaign_symbol_locations(tree, resolved_shape)
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
        table[name] = None if value is _UNRESOLVED else _argument_literal(value)
    for a, default in zip(arguments.kwonlyargs, arguments.kw_defaults):
        value = None if default is None else literal(default)
        table[a.arg] = None if value is _UNRESOLVED else _argument_literal(value)
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
def parameter_signature(node):
    """Preserve binding order and missing defaults independently of JSON object order/null."""
    args = node.args
    positional = args.posonlyargs + args.args
    required = positional[:len(positional) - len(args.defaults)]
    return {
        'parameter_order': [arg.arg for arg in positional if arg.arg != 'self'],
        'required_parameters': [arg.arg for arg in required if arg.arg != 'self'] +
                               [arg.arg for arg, default in zip(args.kwonlyargs, args.kw_defaults)
                                if default is None],
        'keyword_only_parameters': [arg.arg for arg in args.kwonlyargs],
        'positional_only_parameters': [arg.arg for arg in args.posonlyargs if arg.arg != 'self'],
    }
class PlanArgumentError(ValueError):
    """The current plan cannot represent Python call arguments without losing semantics."""
def _argument_literal(value):
    """JSON arrays are Python lists; tuples need a marker because upstream treats them differently."""
    if isinstance(value, tuple):
        return {'__tuple__': [_argument_literal(item) for item in value]}
    if isinstance(value, list):
        return [_argument_literal(item) for item in value]
    if isinstance(value, dict):
        if any(not isinstance(key, str) for key in value):
            raise PlanArgumentError('argument dictionary requires string keys')
        return {key: _argument_literal(item) for key, item in value.items()}
    if value is None or isinstance(value, (str, bool, int, float)):
        return value
    raise PlanArgumentError(f'unsupported literal type: {type(value).__name__}')
def call_args(node: ast.Call, resolve=None):
    """调用实参 → {位置参数: [...], 关键字参数: {...}}。

    字面量直接取；`self.<NAME>` 这类**类属性链上的字面量**经 `resolve` 解析（见
    `campaign_literal_attributes`）；表示不了的参数显式失败，不能丢掉展开参数或冒充完整计划。
    """
    def value_of(argument):
        value = literal(argument)
        if value is not _UNRESOLVED:
            return _argument_literal(value)
        if resolve is not None:
            resolved = resolve(argument)
            if resolved is not _UNRESOLVED:
                return _argument_literal(resolved)
        raise PlanArgumentError(f'unsupported argument: {_brief(argument)}')

    pos, kw = [], {}
    for a in node.args:
        if isinstance(a, ast.Starred):
            expanded = literal(a.value)
            if not isinstance(expanded, (tuple, list)):
                raise PlanArgumentError(f'unsupported positional unpacking: {_brief(a)}')
            pos.extend(_argument_literal(item) for item in expanded)
        else:
            pos.append(value_of(a))
    for k in node.keywords:
        if k.arg is None:
            expanded = literal(k.value)
            if not isinstance(expanded, dict) or any(not isinstance(key, str) for key in expanded):
                raise PlanArgumentError(f'unsupported keyword unpacking: {_brief(k.value)}')
            expanded = {key: _argument_literal(value) for key, value in expanded.items()}
        else:
            expanded = {k.arg: value_of(k.value)}
        if kw.keys() & expanded.keys():
            raise PlanArgumentError('duplicate keyword arguments')
        kw.update(expanded)
    return {'positional': pos, 'keyword': kw}

from export_upstream_data import _UNRESOLVED, _brief, call_name, campaign_map_shape, literal

def _is_bare_return_true(body) -> bool:
    """判断分支体是否只是 `return True`（上游生成器模板的唯一条件体形态）。"""
    stmts = [s for s in body
             if not (isinstance(s, ast.Expr) and isinstance(s.value, ast.Constant))]
    if len(stmts) != 1:
        return False
    s = stmts[0]
    return (isinstance(s, ast.Return) and isinstance(s.value, ast.Constant)
            and s.value.value is True)


def _state_expression(node, resolve, locals_=None):
    """把 `self.X = …` 右值归一成值表达式；表示不了返回 None（调用方记未解析）。"""
    if isinstance(node, ast.Constant) and (node.value is True or node.value is False
                                           or node.value is None or isinstance(node.value, int)):
        return {'literal': node.value}
    if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) \
            and node.func.id == 'SelectedGrids' and len(node.args) == 1 and not node.keywords:
        # `SelectedGrids([A2, H3])` —— 显式格列表（元素用已有的符号解析换坐标）
        items = node.args[0]
        if isinstance(items, (ast.List, ast.Tuple)):
            cells = []
            for element in items.elts:
                resolved = resolve(element)
                if isinstance(resolved, dict) and '__grid__' in resolved:
                    cells.append(resolved)
                else:
                    return None
            return {'grids': cells}
    if isinstance(node, ast.Subscript) and isinstance(node.value, ast.Name) \
            and locals_ and node.value.id in locals_:
        index = node.slice
        if isinstance(index, ast.Constant) and isinstance(index.value, int):
            # `boss = boss[0]` —— 局部集合取下标
            return {'local_index': {'name': node.value.id, 'index': index.value}}
    if isinstance(node, ast.Name):
        # 局部名当值：`boss == A1` 里的 `boss`（局部集合取过下标后就是单个格子）
        if locals_ and node.id in locals_:
            return {'local': node.id}
        resolved = resolve(node)
        if isinstance(resolved, dict) and '__grid__' in resolved:
            # 裸格名当值（`A1`）
            return {'grid': resolved}
    if isinstance(node, ast.Compare) and len(node.ops) == 1 and len(node.comparators) == 1:
        # 比较也能作为**值表达式**：复合条件里要用
        # （`self.mystery_count < 1 and self.clear_roadblocks([road_MY])`）。与 `branch_test` 同一套编码。
        operators = {ast.GtE: '>=', ast.Gt: '>', ast.LtE: '<=', ast.Lt: '<',
                     ast.Eq: '==', ast.NotEq: '!='}
        left = _state_expression(node.left, resolve, locals_)
        right = _state_expression(node.comparators[0], resolve, locals_)
        if left is not None and right is not None and type(node.ops[0]) in operators:
            return {'compare': {'left': left, 'op': operators[type(node.ops[0])], 'right': right}}
    if is_self_call(node):
        # 调用作为值：真假/数值由执行器调原语得到（实参仍要能静态表达）。
        # 条件里的 `self.fleet_at(A3, fleet=2) and A2.is_mystery` 就靠这一支。
        return {'call': {'op': call_name(node), 'args': call_args(node, resolve)}}
    if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name):
        # `A1.enemy_scale` 这类**格子属性**读取（裸格名是模块级 `= MAP.flatten()` 的绑定）
        resolved = resolve(node.value)
        if isinstance(resolved, dict) and '__grid__' in resolved:
            return {'grid_attr': {'grid': resolved, 'name': node.attr}}
    if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
        inner = _state_expression(node.operand, resolve, locals_)
        return {'not': inner} if inner is not None else None
    if isinstance(node, ast.BoolOp) and len(node.values) >= 2:
        parts = [_state_expression(value, resolve, locals_) for value in node.values]
        if any(part is None for part in parts):
            return None
        return {'and' if isinstance(node.op, ast.And) else 'or': parts}
    if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) \
            and node.value.id == 'self':
        if node.attr == 'map_is_clear_mode':
            return {'runtime': 'map_is_clear_mode'}
        if node.attr in ('battle_count', 'mystery_count'):
            # 宿主状态（不是关卡实例属性）：执行器从宿主取（`battle_count` / `mystery_count`）
            return {'host_value': node.attr}
        return {'state': node.attr}
    if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Attribute) \
            and isinstance(node.value.value, ast.Name) and node.value.value.id == 'self' \
            and node.value.attr == 'config':
        return {'config': node.attr}
    return None


def _brief(node, limit: int = 70) -> str:
    """未解析语句的**简短源码**（进 `unparsed`，便于按具体形态聚合与定位）。"""
    try:
        text = ast.unparse(node)
    except Exception:                                   # noqa: BLE001 —— 反解析失败不该影响导出
        return '<?>'
    text = re.sub(r'\s+', ' ', text)
    return text[:limit]


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
    resolve = resolve or (lambda _: _UNRESOLVED)

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
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) \
                and node.value.id == 'self' and node.attr == 'map_is_clear_mode':
            # 运行期标志（上游 FastForwardHandler.handle_fast_forward 设置），
            # 见 C# `CampaignRuntimeConfig.MapIsClearMode` 的语义说明
            return {'runtime': 'map_is_clear_mode', 'negate': negate}
        local = _local_reference(node, locals_)
        if local is not None:
            if '__index__' in local:
                return {'expr': {'local_index': {'name': local['__local__'], 'index': local['__index__']}},
                        'negate': negate}
            return {'local': local['__local__'], 'negate': negate}
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) \
                and node.value.id == 'self':
            # `if self.<属性>:` —— 实例属性/运行期标志的真假（值表达式统一走 `expr`）
            expression = _state_expression(node, arg_resolve, locals_)
            if expression is not None:
                return {'expr': expression, 'negate': negate}
        if isinstance(node, ast.BoolOp) and len(node.values) >= 2:
            # `A and B` / `A or B`（含括号）：整句编码成值表达式，短路语义由执行器照 Python 处理
            expression = _state_expression(node, arg_resolve, locals_)
            if expression is not None:
                return {'expr': expression, 'negate': negate}
        if isinstance(node, ast.Compare) and len(node.ops) == 1 and len(node.comparators) == 1:
            # 通用比较：左右都必须是能表达的值表达式（`self.fleet_step >= 3`、`A1.enemy_scale != 3`）
            operators = {ast.GtE: '>=', ast.Gt: '>', ast.LtE: '<=', ast.Lt: '<',
                         ast.Eq: '==', ast.NotEq: '!='}
            left = _state_expression(node.left, arg_resolve, locals_)
            right = _state_expression(node.comparators[0], arg_resolve, locals_)
            if left is not None and right is not None and type(node.ops[0]) in operators:
                return {'expr': {'compare': {'left': left, 'op': operators[type(node.ops[0])],
                                             'right': right}},
                        'negate': negate}
        if isinstance(node, ast.Compare) and isinstance(node.left, ast.Attribute) \
                and isinstance(node.left.value, ast.Name) and node.left.value.id == 'self' \
                and node.left.attr == 'battle_count' and len(node.ops) == 1 and len(node.comparators) == 1:
            # `self.battle_count >= 3` 这类**状态比较**：C# 侧 `host.BattleCount` 就是它
            right = node.comparators[0]
            operator = {ast.GtE: '>=', ast.Gt: '>', ast.LtE: '<=', ast.Lt: '<', ast.Eq: '==', ast.NotEq: '!='}
            if isinstance(right, ast.Constant) and isinstance(right.value, int) and type(node.ops[0]) in operator:
                return {'battle_count': {'op': operator[type(node.ops[0])], 'value': right.value},
                        'negate': negate}
            if isinstance(right, (ast.List, ast.Tuple)) and isinstance(node.ops[0], (ast.In, ast.NotIn)):
                values = [item.value for item in right.elts
                          if isinstance(item, ast.Constant) and isinstance(item.value, int)]
                if len(values) == len(right.elts):
                    return {'battle_count_in': values,
                            'negate': not negate if isinstance(node.ops[0], ast.NotIn) else negate}
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name):
            # `<GRID>.is_xxx`：裸格名是模块级 `A1, B1, ... = MAP.flatten()` 绑定的格子对象
            # （`campaign_15_1.py:40` 那一片）。用**已有的符号解析**把格名换成坐标，再带属性名。
            resolved = resolve(node.value)
            if isinstance(resolved, dict) and '__grid__' in resolved and node.attr.startswith('is_'):
                return {'grid': resolved, 'attr': node.attr, 'negate': negate}
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Attribute) \
                and isinstance(node.value.value, ast.Name) and node.value.value.id == 'self' \
                and node.value.attr == 'config':
            # `self.config.MAP_HAS_MOVABLE_ENEMY` 这类**配置读取**：C# 侧配置里都有对应字段
            return {'config': node.attr, 'negate': negate}
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
                    unparsed.append(f'If(cond)@{stmt.lineno}: {_brief(stmt.test)}')
                    continue
                then_steps, then_unparsed, then_dead, then_end = normalize(stmt.body)
                else_steps, else_unparsed, else_dead, else_end = normalize(stmt.orelse)
                if then_unparsed or else_unparsed:
                    # 任一分支表示不了 → 整条 if 记为未解析（附加原因便于定位）
                    unparsed.append(f'If(nested)@{stmt.lineno}: {_brief(stmt.test)}')
                    unparsed.extend(then_unparsed)
                    unparsed.extend(else_unparsed)
                    continue
                steps.append({'kind': 'branch', 'test': test,
                              'body': then_steps, 'orelse': else_steps})
                dead.extend(then_dead)
                dead.extend(else_dead)
                # 只有**两条分支都必然返回**时，后面的语句才不可达
                if then_end and stmt.orelse and else_end:
                    terminated = True
            elif isinstance(stmt, ast.Return):
                if stmt.value is not None and is_self_call(stmt.value):
                    steps.append(self_call_step(stmt.value, 'terminal'))
                    terminated = True
                elif stmt.value is None or (isinstance(stmt.value, ast.Constant) and (
                        stmt.value.value is True or stmt.value.value is False or stmt.value.value is None)):
                    # `return True` / `return False` / `return None`：字面量返回，直接进计划
                    # （上游不少钩子以 `return True` 收尾，以前一律记 Return(expr) 把整份计划作废）
                    steps.append({'kind': 'return', 'value': stmt.value.value if stmt.value else None})
                    terminated = True
                elif is_super_delegate(stmt.value):
                    # `return super().X(...)`：纯委托，本类没有新增逻辑，只是覆写钩子。
                    steps.append({'op': super_call_name(stmt.value), 'args': call_args(stmt.value, arg_resolve),
                                  'kind': 'super_delegate'})
                    terminated = True
                else:
                    unparsed.append(f'Return(expr)@{stmt.lineno}: {_brief(stmt.value)}')
            elif isinstance(stmt, ast.Expr):
                if is_self_call(stmt.value):
                    steps.append(self_call_step(stmt.value, 'call'))
                else:
                    if isinstance(stmt.value, ast.Call) and (
                            (isinstance(stmt.value.func, ast.Attribute)
                             and isinstance(stmt.value.func.value, ast.Name)
                             and stmt.value.func.value.id == 'logger')
                            or (isinstance(stmt.value.func, ast.Name) and stmt.value.func.id == 'print')):
                        # 纯日志调用：没有引擎副作用（不改地图状态、不发设备动作），但**记进计划**，
                        # 免得"静默丢掉"——执行器只把它写进步骤日志。
                        # 嵌套调用/动态求值可能有引擎副作用，不能以“日志”之名吞掉。
                        call_args(stmt.value)
                        steps.append({'kind': 'log', 'text': _brief(stmt.value, 120)})
                    else:
                        unparsed.append(f'Expr@{stmt.lineno}: {_brief(stmt.value)}')
            elif isinstance(stmt, ast.Assign) and len(stmt.targets) == 1 \
                    and isinstance(stmt.targets[0], ast.Attribute) \
                    and isinstance(stmt.targets[0].value, ast.Name) \
                    and stmt.targets[0].value.id == 'self':
                # `self.<属性> = <值表达式>` —— 关卡实例属性（跨钩子存在，属**状态**不是局部变量）
                expression = _state_expression(stmt.value, arg_resolve, locals_)
                if expression is None:
                    unparsed.append(f'Assign@{stmt.lineno}: {_brief(stmt)}')
                else:
                    steps.append({'kind': 'state_set', 'name': stmt.targets[0].attr,
                                  'expr': expression})
            elif isinstance(stmt, ast.Assign):
                v = stmt.value
                if is_self_call(v) and len(stmt.targets) == 1 \
                        and isinstance(stmt.targets[0], ast.Name):
                    target = stmt.targets[0].id
                    steps.append(self_call_step(v, 'assign', target=target))
                    # 记成局部变量：后面的 `if <name>:` / `<name>[0]` 才算得出来
                    locals_.add(target)
                else:
                    # 不是自身调用 → 试"局部名绑定值表达式"（`ignore = None` / `ignore = SelectedGrids([A2])` /
                    # `boss = boss[0]`）。**必须放在这里**：上面那个"通用 Assign 分支"会先接住所有赋值，
                    # 单独立一支会被它挡住（实测：`ignore = None` 一直被记 unparsed）。
                    if len(stmt.targets) == 1 and isinstance(stmt.targets[0], ast.Name):
                        bind = stmt.targets[0].id
                        expression = _state_expression(stmt.value, arg_resolve, locals_)
                        if expression is not None:
                            steps.append({'kind': 'local_set', 'target': bind, 'expr': expression})
                            locals_.add(bind)
                            continue
                    unparsed.append(f'Assign@{stmt.lineno}: {_brief(stmt)}')
            elif isinstance(stmt, ast.For):
                # `for grid in self.map: grid.<flag> = <字面量>` —— 整图设一个布尔标志（识别提示）。
                # 只认这一种形态：循环目标是单个名字、迭代对象是 `self.map`、循环体只有一条
                # `grid.<flag> = 字面量`；别的循环一律照旧记未解析（不猜）。
                target = stmt.target
                body = [s for s in stmt.body
                        if not (isinstance(s, ast.Expr) and isinstance(s.value, ast.Constant))]
                ok = (isinstance(target, ast.Name) and isinstance(stmt.iter, ast.Attribute)
                      and isinstance(stmt.iter.value, ast.Name) and stmt.iter.value.id == 'self'
                      and stmt.iter.attr == 'map' and not stmt.orelse and len(body) == 1)
                if ok and isinstance(body[0], ast.Assign) and len(body[0].targets) == 1 \
                        and isinstance(body[0].targets[0], ast.Attribute) \
                        and isinstance(body[0].targets[0].value, ast.Name) \
                        and body[0].targets[0].value.id == target.id \
                        and isinstance(body[0].value, ast.Constant) \
                        and isinstance(body[0].value.value, bool):
                    steps.append({'kind': 'map_set', 'flag': body[0].targets[0].attr,
                                  'value': body[0].value.value})
                else:
                    unparsed.append(f'For@{stmt.lineno}: {_brief(stmt)}')
            elif isinstance(stmt, ast.Pass):
                continue
            else:
                if isinstance(stmt, ast.Raise) and isinstance(stmt.exc, ast.Call) \
                        and isinstance(stmt.exc.func, ast.Name) \
                        and stmt.exc.func.id in ('CampaignEnd', 'MapEnemyMoved') \
                        and not stmt.exc.args and not stmt.exc.keywords and stmt.cause is None:
                    # 上游用异常做控制流：`raise CampaignEnd()` 结束本关、`raise MapEnemyMoved()` 让
                    # `execute_a_battle` 重新识别地图并重试。两者语义不同，都按**信号步骤**记下来，
                    # 由执行器抛对应的控制流信号（不当作一次调用）。
                    steps.append({'kind': 'raise', 'signal': stmt.exc.func.id})
                    terminated = True
                else:
                    unparsed.append(f'{type(stmt).__name__}@{stmt.lineno}: {_brief(stmt)}')

        return steps, unparsed, dead, terminated

    try:
        steps, unparsed, dead, terminated = normalize(body)
    except PlanArgumentError as error:
        steps, unparsed, dead = [], [f'Arguments({where}): {error}'], []
    plan_complete = not unparsed
    if not plan_complete:
        steps = []
    return steps, plan_complete, unparsed, dead



def campaign_method_plans(tree, module: str, root: str, shape: str | None = None):
    """Reproducible declared method plans, shared by export and corruption checks."""
    literal_resolver = attribute_literal_resolver(campaign_literal_attributes(tree, module, root))
    road_table = campaign_road_table(tree, shape)
    road_resolver = road_argument_resolver(road_table, campaign_road_list_variables(tree, road_table))
    grid_locations = campaign_symbol_locations(tree, shape)
    symbol_resolver = symbol_argument_resolver(
        grid_locations, campaign_grid_list_variables(tree, grid_locations))

    def resolve_argument(node):
        for resolver in (literal_resolver, road_resolver, symbol_resolver):
            value = resolver(node)
            if value is not _UNRESOLVED:
                return value
        return _UNRESOLVED

    battles = []
    for node in tree.body:
        if not isinstance(node, ast.ClassDef) or node.name != 'Campaign':
            continue
        for sub in node.body:
            if not isinstance(sub, (ast.FunctionDef, ast.AsyncFunctionDef)):
                continue
            body = [item for item in sub.body if not (isinstance(item, ast.Expr)
                    and isinstance(item.value, ast.Constant))]
            signature_error = None
            try:
                parameters = parameter_defaults(sub)
            except PlanArgumentError as error:
                parameters, signature_error = {}, str(error)
            parameter_ref = parameter_resolver(parameters)

            def resolve_with_params(value):
                resolved = parameter_ref(value)
                return resolved if resolved is not _UNRESOLVED else resolve_argument(value)

            steps, complete, unparsed, dead = derive_plan(body, sub.name, resolve_with_params)
            if signature_error:
                unparsed.append('method parameter default: ' + signature_error)
            if isinstance(sub, ast.AsyncFunctionDef) or sub.decorator_list:
                unparsed.append('method transformation requires native execution')
            if sub.args.vararg or sub.args.kwarg:
                unparsed.append('variadic method signature requires native execution')
            if any(default is not None and literal(default) is _UNRESOLVED
                   for default in [*sub.args.defaults, *sub.args.kw_defaults]):
                unparsed.append('method parameter default is not a literal')
            if unparsed:
                steps, complete = [], False
            battles.append({
                'method': sub.name, 'calls': [call_name(call) for call in ast.walk(sub) if is_self_call(call)],
                'steps': steps, 'plan_complete': complete, 'unparsed': unparsed,
                'dead_code': dead, 'parameters': parameters, 'stmt_count': len(body),
                **parameter_signature(sub),
            })
    return battles
