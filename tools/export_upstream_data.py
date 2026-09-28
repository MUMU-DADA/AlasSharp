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
    <out>/campaign/<path>.json         关卡声明（MAP + Config + Campaign 来源与方法签名）
    <out>/campaign_index.json          关卡索引（来源、方法和完整性摘要）
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

EXPORTER_VERSION = '3.0.0'
SERVERS = ('cn', 'en', 'jp', 'tw')
SKIP_DIRS = {'.venv', '.git', '__pycache__', '.pytest_cache', '.ruff_cache', '.trial-merge'}

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
    这里从路径派生一个稳定标识，并在声明里用 name_source 标注来源，避免冒充权威。
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


def _brief(node, limit: int = 70) -> str:
    """Compact source text for unresolved offline declarations."""
    try:
        text = ast.unparse(node)
    except Exception:
        return '<?>'
    text = re.sub(r'\s+', ' ', text)
    return text[:limit]


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
















def campaign_map_shape(tree, resolved: str | None = None) -> str:
    """模块级 `MAP.shape = 'K9'`；本模块没写时用**解析出来的**形状
    （上游有 `MAP = copy.copy(MAP_15_4)` 这种继承写法，只读本模块会拿不到）。"""
    if resolved:
        return resolved
    for node in tree.body:
        if not isinstance(node, ast.Assign) or not isinstance(node.value, ast.Constant) \
                or not isinstance(node.value.value, str):
            continue
        for target in node.targets:
            if isinstance(target, ast.Attribute) and target.attr == 'shape':
                return node.value.value
    return ''


def campaign_class_state_defaults(declarations) -> dict:
    """Resolved declared scalar defaults only; inherited/native state remains outside this summary.

    Reuse CampaignResolver's binding/type provenance so overrides, expressions,
    annotations and method shadowing cannot disagree with attributes_meta.
    """
    return {name: value for name, value in declarations['values'].items()
            if value is None or type(value) in (bool, int, str)}




























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
def page_documents(root: str):
    """导出上游页面图：页面（含校验按钮）与页面之间的跳转边。

    只做**静态提取**（不导入上游代码）：`page_x = Page(CHECK_BUTTON)` 与
    `page_x.link(button=…, destination=…)`。这是第三阶段「只依赖上游静态规则」要用的数据之一；
    运行时导航仍然走上游对象与原生流程，本导出只用于离线展示、溯源与漂移校验。
    """
    source = os.path.join(root, 'module', 'ui', 'page.py')
    if not os.path.isfile(source):
        return None, {'present': False, 'reason': 'module/ui/page.py 不存在'}
    with open(source, encoding='utf-8') as handle:
        tree = ast.parse(handle.read())

    pages, edges, unresolved = {}, [], []

    def arguments(call, names):
        if len(call.args) > len(names) or any(isinstance(arg, ast.Starred) for arg in call.args):
            return None
        result = dict(zip(names, call.args))
        for keyword in call.keywords:
            if keyword.arg not in names or keyword.arg in result:
                return None
            result[keyword.arg] = keyword.value
        return result if set(result) == set(names) else None

    def reference(node, allow_none=False):
        if isinstance(node, ast.Name):
            return node.id
        if allow_none and isinstance(node, ast.Constant) and node.value is None:
            return 'None'
        if isinstance(node, ast.Attribute) and reference(node.value) is not None:
            return ast.unparse(node)
        return None

    for node in tree.body:
        if isinstance(node, ast.Assign) and len(node.targets) == 1 \
                and isinstance(node.targets[0], ast.Name) \
                and isinstance(node.value, ast.Call) \
                and isinstance(node.value.func, ast.Name) and node.value.func.id == 'Page':
            name = node.targets[0].id
            args = arguments(node.value, ('check_button',))
            check = reference(args['check_button'], allow_none=True) if args else None
            if check is None or name in pages:
                unresolved.append(f'Page@{node.lineno}: unsupported or duplicate declaration {name}')
            else:
                pages[name] = {'name': name, 'check_button': check, 'links': []}
            continue
        if isinstance(node, ast.Expr) and isinstance(node.value, ast.Call) \
                and isinstance(node.value.func, ast.Attribute) and node.value.func.attr == 'link':
            owner = reference(node.value.func.value)
            pair = arguments(node.value, ('button', 'destination'))
            button = reference(pair['button']) if pair else None
            destination = reference(pair['destination']) if pair else None
            if owner not in pages or not button or not destination:
                unresolved.append(f'link@{node.lineno}: unsupported arguments or unknown owner')
                continue
            edges.append((owner, button, destination))
            if destination not in pages:
                unresolved.append(f'link@{node.lineno}: destination is not defined yet: {destination}')
                continue
            # Page.links 是以目标 Page 为键的字典，同一目标后一次写入覆盖按钮，保持首次插入顺序。
            links = pages[owner]['links']
            previous = next((item for item in links if item['destination'] == destination), None)
            if previous is None:
                links.append({'button': button, 'destination': destination})
            else:
                previous['button'] = button
            continue
        if isinstance(node, (ast.Import, ast.ImportFrom, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            continue
        if isinstance(node, ast.Expr) and isinstance(node.value, ast.Constant):
            continue
        # 没有执行任意顶层语句的静态解释器：未知赋值、分支或调用都可能改图，必须留痕。
        unresolved.append(f'{type(node).__name__}@{node.lineno}: {_brief(node)}')

    records = list(pages.values())
    source_hashes = {os.path.relpath(source, root).replace('\\', '/'): sha256_file(source)}
    document = {
        'version': EXPORTER_VERSION,
        'source_files': source_hashes,
        'pages': records,
    }
    metadata = {
        'present': True,
        'count': len(records),
        'links': sum(len(page['links']) for page in records),
        'dangling': [f'{owner}.{button} → {destination}' for owner, button, destination in edges
                     if destination not in pages],
        'unresolved': unresolved,
        'source_files': 1,
        'source_hashes': source_hashes,
    }
    return document, metadata


def export_pages(root: str, out_dir: str, manifest: dict):
    document, manifest['pages'] = page_documents(root)
    path = os.path.join(out_dir, 'pages.json')
    if document is None:
        if os.path.isfile(path):
            os.unlink(path)
    else:
        _write_json(path, document)


def campaign_method_declarations(tree):
    """Return source declarations without interpreting method bodies.

    Campaign methods are executable C# rule responsibilities.  The exporter
    records only provenance and signatures for offline review; it never turns
    Python statements into an executable JSON plan.
    """
    methods = []
    for node in tree.body:
        if not isinstance(node, ast.ClassDef) or node.name != 'Campaign':
            continue
        for member in node.body:
            if not isinstance(member, (ast.FunctionDef, ast.AsyncFunctionDef)):
                continue
            args = member.args
            positional = [arg for arg in (*args.posonlyargs, *args.args) if arg.arg != 'self']
            methods.append({
                'method': member.name,
                'kind': 'battle' if member.name.startswith('battle_') else 'hook',
                'async': isinstance(member, ast.AsyncFunctionDef),
                'decorators': [ast.unparse(item) for item in member.decorator_list],
                'parameter_order': [arg.arg for arg in positional] + [arg.arg for arg in args.kwonlyargs],
                'required_parameters': [arg.arg for arg in positional[len(args.defaults):]] +
                                       [arg.arg for arg, default in zip(args.kwonlyargs, args.kw_defaults)
                                        if default is None],
                'keyword_only_parameters': [arg.arg for arg in args.kwonlyargs],
                'positional_only_parameters': [arg.arg for arg in args.posonlyargs if arg.arg != 'self'],
                'variadic': bool(args.vararg or args.kwarg),
                'calls': sorted({call_name(call) for call in ast.walk(member)
                                 if isinstance(call, ast.Call) and call_name(call)}),
                'line': member.lineno,
                'end_line': getattr(member, 'end_lineno', member.lineno),
            })
    return methods


def export_campaign(root: str, out_dir: str, manifest: dict):
    """Export MAP/Config/Campaign declarations for offline provenance.

    Campaign method bodies stay in their upstream source files and are
    migrated directly into typed C# rules.  This function intentionally emits
    no steps, plans, tiers, or executable intermediate representation.
    """
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
        map_export = map_resolver.export('campaign.' + module)
        methods = campaign_method_declarations(tree)
        ir = {'source': rel, 'name': None, '_name_source': None, 'map': {}, 'config': {},
              'config_meta': {}, 'campaign': {'methods': methods, 'attributes': {}},
              'unresolved': []}

        for node in tree.body:
            if isinstance(node, ast.ClassDef) and node.name == 'Campaign':
                ir['campaign']['class'] = node.name
                ir['campaign']['bases'] = [ast.unparse(base) for base in node.bases]

        declarations = campaign_resolver.export('campaign.' + module)
        ir['campaign']['attributes'] = declarations['values']
        ir['campaign']['attributes_meta'] = {key: value for key, value in declarations.items()
                                             if key != 'values'}
        ir['campaign']['initial_state'] = campaign_class_state_defaults(declarations)
        for issue in declarations['unresolved']:
            prefix = 'Campaign.' + issue['field'] if 'field' in issue else 'Campaign'
            ir['unresolved'].append(f"{prefix}: {issue.get('reason', issue)}")

        ir['map'] = map_export['values']
        ir['map_meta'] = {key: map_export[key] for key in (
            'present', 'complete', 'origins', 'typed_values', 'source_files',
            'unresolved', 'derived_from', 'calls')}
        if map_export['name'] is not None:
            ir['name'], ir['_name_source'] = map_export['name'], 'CampaignMap'
        for issue in map_export['unresolved']:
            prefix = 'MAP.' + issue['field'] if 'field' in issue else 'MAP'
            ir['unresolved'].append(f"{prefix}: {issue.get('reason', issue)}")

        config_export = config_resolver.export('campaign.' + module)
        ir['config'] = config_export['values']
        ir['config_meta'] = {key: config_export[key] for key in (
            'present', 'complete', 'mro', 'origins', 'typed_values',
            'source_files', 'unresolved')}
        if not config_export['complete']:
            for issue in config_export['unresolved']:
                field = issue.get('field')
                prefix = f'Config.{field}' if field else 'Config'
                ir['unresolved'].append(f"{prefix}: {issue.get('reason', issue)}")

        if not ir['name']:
            ir['name'] = name_from_path(rel)
            ir['_name_source'] = 'path'

        battle_methods = [method for method in methods if method['kind'] == 'battle']
        if not battle_methods:
            ir['unresolved'].append('无 Campaign.battle_* 方法')
        if ir['unresolved']:
            unresolved_all.append({'file': rel, 'items': ir['unresolved']})

        stats['files'] += 1
        stats['method_count'] += len(methods)
        stats['battle_method_count'] += len(battle_methods)
        stats['hook_method_count'] += len(methods) - len(battle_methods)
        stats['with_unresolved'] += bool(ir['unresolved'])

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
            'method_count': len(methods),
            'battle_methods': [method['method'] for method in battle_methods],
            'hook_methods': [method['method'] for method in methods if method['kind'] == 'hook'],
            'config_keys': sorted(ir['config'].keys()),
            'config_present': ir['config_meta']['present'],
            'config_complete': (ir['config_meta']['present'] and ir['config_meta']['complete']),
            'map_keys': sorted(ir['map'].keys()),
            'map_present': ir['map_meta']['present'],
            'map_complete': ir['map_meta']['present'] and ir['map_meta']['complete'],
            'campaign_attributes': sorted(declarations['values']),
            'campaign_present': declarations['present'],
            'campaign_complete': declarations['present'] and declarations['complete'],
            'campaign_aliases': sorted(declarations['method_aliases']),
            'needs_review': bool(ir['unresolved']) or not ir['config_meta']['complete']
                            or not ir['map_meta']['complete'] or not declarations['complete'],
        })

    index.sort(key=lambda row: row['source'])
    _write_json(os.path.join(out_dir, 'campaign_index.json'),
                {'version': EXPORTER_VERSION, 'chapters': index})

    manifest['campaign'] = {
        **stats,
        'config_modules': sum(1 for row in index if row['config_present']),
        'config_complete': sum(1 for row in index if row['config_present'] and row['config_complete']),
        'config_fields': sum(len(row['config_keys']) for row in index),
        'map_modules': sum(1 for row in index if row['map_present']),
        'map_complete': sum(1 for row in index if row['map_complete']),
        'map_fields': sum(len(row['map_keys']) for row in index),
        'campaign_modules': sum(row['campaign_present'] for row in index),
        'campaign_complete': sum(row['campaign_complete'] for row in index),
        'campaign_attributes': sum(len(row['campaign_attributes']) for row in index),
        'campaign_aliases': sum(len(row['campaign_aliases']) for row in index),
        'needs_review': sum(1 for row in index if row['needs_review']),
        'unresolved_detail': unresolved_all,
        'source_files': len(source_files),
        'source_hashes': {os.path.relpath(path, root).replace('\\', '/'): sha256_file(path)
                          for path in source_files},
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
    'title': 'ALAS upstream campaign declaration contract',
    'description': 'MAP、Config 与 Campaign 来源声明；Campaign 方法体必须直接迁移到 C#，这里不生成执行计划。',
    'type': 'object',
    'required': ['source', 'map', 'map_meta', 'config', 'config_meta', 'campaign'],
    'properties': {
        'source': {'type': 'string'},
        'name': {'type': ['string', 'null']},
        'name_source': {'enum': ['CampaignMap', 'path']},
        'map': {'type': 'object', 'additionalProperties': True},
        'map_meta': {
            'type': 'object',
            'required': ['present', 'complete', 'origins', 'typed_values', 'source_files',
                         'unresolved', 'derived_from', 'calls'],
            'properties': {
                'present': {'type': 'boolean'}, 'complete': {'type': 'boolean'},
                'derived_from': {'type': ['string', 'null']},
                'calls': {'type': 'array', 'items': {'type': 'object'}},
                'origins': {'type': 'object', 'additionalProperties': {'type': 'object'}},
                'typed_values': {'type': 'object', 'additionalProperties': True},
                'source_files': {'type': 'array', 'items': {'type': 'string'}},
                'unresolved': {'type': 'array', 'items': {'type': 'object'}},
            },
        },
        'config': {'type': 'object', 'additionalProperties': True},
        'config_meta': {
            'type': 'object',
            'required': ['present', 'complete', 'mro', 'origins', 'typed_values',
                         'source_files', 'unresolved'],
            'properties': {
                'present': {'type': 'boolean'}, 'complete': {'type': 'boolean'},
                'mro': {'type': 'array', 'items': {'type': 'string'}},
                'origins': {'type': 'object', 'additionalProperties': {'type': 'object'}},
                'typed_values': {'type': 'object', 'additionalProperties': True},
                'source_files': {'type': 'array', 'items': {'type': 'string'}},
                'unresolved': {'type': 'array', 'items': {'type': 'object'}},
            },
        },
        'campaign': {
            'type': 'object',
            'required': ['methods', 'attributes', 'attributes_meta', 'initial_state'],
            'properties': {
                'class': {'type': 'string'},
                'bases': {'type': 'array', 'items': {'type': 'string'}},
                'initial_state': {'type': 'object', 'additionalProperties': {
                    'type': ['boolean', 'integer', 'string', 'null']}},
                'attributes': {'type': 'object', 'additionalProperties': True},
                'attributes_meta': {
                    'type': 'object',
                    'required': ['scope', 'present', 'complete', 'class_reference', 'origins',
                                 'typed_values', 'method_aliases', 'source_files', 'unresolved'],
                    'properties': {
                        'scope': {'const': 'declared'}, 'present': {'type': 'boolean'},
                        'complete': {'type': 'boolean'},
                        'class_reference': {'type': ['string', 'null']},
                        'origins': {'type': 'object', 'additionalProperties': {'type': 'object'}},
                        'typed_values': {'type': 'object'},
                        'method_aliases': {'type': 'object', 'additionalProperties': {'type': 'object'}},
                        'source_files': {'type': 'array', 'items': {'type': 'string'}},
                        'unresolved': {'type': 'array', 'items': {'type': 'object'}},
                    },
                },
                'methods': {
                    'type': 'array',
                    'items': {
                        'type': 'object',
                        'required': ['method', 'kind', 'async', 'decorators', 'parameter_order',
                                     'required_parameters', 'keyword_only_parameters',
                                     'positional_only_parameters', 'variadic', 'calls', 'line', 'end_line'],
                        'properties': {
                            'method': {'type': 'string'},
                            'kind': {'enum': ['battle', 'hook']},
                            'async': {'type': 'boolean'},
                            'decorators': {'type': 'array', 'items': {'type': 'string'}},
                            'parameter_order': {'type': 'array', 'items': {'type': 'string'}},
                            'required_parameters': {'type': 'array', 'items': {'type': 'string'}},
                            'keyword_only_parameters': {'type': 'array', 'items': {'type': 'string'}},
                            'positional_only_parameters': {'type': 'array', 'items': {'type': 'string'}},
                            'variadic': {'type': 'boolean'},
                            'calls': {'type': 'array', 'items': {'type': 'string'}},
                            'line': {'type': 'integer', 'minimum': 1},
                            'end_line': {'type': 'integer', 'minimum': 1},
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
    for name in ('assets', 'campaign', 'pages'):
        manifest[name] = {}
    export_assets(repo, out, manifest)
    export_pages(repo, out, manifest)
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
    OWNED_FILES = {'assets.json', 'campaign_index.json', 'pages.json', 'manifest.json'}
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
